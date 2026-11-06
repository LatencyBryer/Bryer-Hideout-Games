/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace BryersHideoutGames.Browser;

/// <summary>
/// Reusable, shader-readable D3D11 dynamic BGRA textures. Rather than asking
/// ITextureProvider.CreateEmpty(cpuWrite: true) to choose a resource usage,
/// explicitly create ID3D11Texture2D with DYNAMIC + CPU_ACCESS_WRITE and
/// SHADER_RESOURCE binding. This avoids invalid usage/CPU-access combinations
/// on texture-provider backends that do not expose CPU-writable shader resources.
/// All COM calls run on the Dalamud ImGui draw thread.
/// </summary>
internal sealed unsafe class PersistentGpuTextures : IDisposable
{
    private const uint D3D11UsageDynamic = 2;
    private const uint D3D11BindShaderResource = 0x8;
    private const uint D3D11CpuAccessWrite = 0x10000;
    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const uint D3D11MapWriteDiscard = 4;
    private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);

    private readonly IDalamudTextureWrap?[] wraps = new IDalamudTextureWrap?[2];
    private readonly nint[] resources = new nint[2];
    private nint context;
    private int activeIndex = -1;
    private bool disposed;

    public int Width { get; }
    public int Height { get; }
    public IDalamudTextureWrap? Current => activeIndex < 0 ? null : wraps[activeIndex];

    public PersistentGpuTextures(ITextureProvider provider, int width, int height)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("D3D11 texture renderer requires Windows x64.");
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid browser surface dimensions.");
        if (Unsafe.SizeOf<ImTextureID>() != IntPtr.Size)
            throw new PlatformNotSupportedException("Unsupported ImGui texture handle representation.");

        Width = width;
        Height = height;
        try
        {
            // Obtain the active D3D11 device through an SRV created by Dalamud.
            // This temporary texture is only used to acquire the device, then
            // immediately released; no per-frame resource is allocated by Dalamud.
            var specs = RawImageSpecification.Bgra32(1, 1);
            using var bootstrap = provider.CreateEmpty(specs, cpuRead: false, cpuWrite: false,
                debugName: "BryerGames D3D11 bootstrap (temporary)");
            var id = bootstrap.Handle;
            var bootstrapSrv = Unsafe.As<ImTextureID, nint>(ref id);
            if (bootstrapSrv == 0)
                throw new InvalidOperationException("D3D11 bootstrap shader resource view is null.");

            nint device = 0;
            var bootstrapResource = GetResourceFromSrv(bootstrapSrv);
            if (bootstrapResource == 0)
                throw new InvalidOperationException("D3D11 bootstrap resource is null.");
            try
            {
                var getDevice = (delegate* unmanaged[Stdcall]<nint, nint*, void>)Vtable(bootstrapResource)[3];
                getDevice(bootstrapResource, &device);
            }
            finally { Release(bootstrapResource); }

            if (device == 0)
                throw new InvalidOperationException("D3D11 device is unavailable.");
            try
            {
                var getContext = (delegate* unmanaged[Stdcall]<nint, nint*, void>)Vtable(device)[40];
                nint immediateContext = 0;
                getContext(device, &immediateContext);
                context = immediateContext; // GetImmediateContext returns an AddRef'd pointer.
                if (context == 0)
                    throw new InvalidOperationException("D3D11 immediate context is unavailable.");

                var desc = new Texture2DDesc
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DxgiFormatB8G8R8A8Unorm,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Usage = D3D11UsageDynamic,
                    BindFlags = D3D11BindShaderResource,
                    CpuAccessFlags = D3D11CpuAccessWrite,
                    MiscFlags = 0,
                };

                var createTexture2D = (delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Vtable(device)[5];
                var createSrv = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vtable(device)[7];
                for (var i = 0; i < resources.Length; i++)
                {
                    nint texture = 0;
                    var hr = createTexture2D(device, &desc, 0, &texture);
                    if (hr < 0 || texture == 0)
                        throw new InvalidOperationException($"D3D11 CreateTexture2D failed (HRESULT 0x{hr:X8}).");
                    resources[i] = texture;

                    nint srv = 0;
                    try
                    {
                        hr = createSrv(device, texture, 0, &srv);
                        if (hr < 0 || srv == 0)
                            throw new InvalidOperationException($"D3D11 CreateShaderResourceView failed (HRESULT 0x{hr:X8}).");
                        // Transfer ownership of the SRV reference to the wrap.
                        wraps[i] = new NativeSrvWrap(srv, width, height);
                        srv = 0;
                    }
                    finally { Release(srv); }
                }
            }
            finally { Release(device); }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Updates the next dynamic texture using WRITE_DISCARD with MapFlags=0.
    /// Direct3D 11 explicitly disallows DO_NOT_WAIT with WRITE_DISCARD:
    /// using both returns E_INVALIDARG (0x80070057) on supported drivers.
    /// Map may briefly synchronize with the GPU; the two textures reduce contention.
    /// </summary>
    public bool TryUpdate(ReadOnlySpan<byte> bgraPixels)
    {
        if (disposed) throw new ObjectDisposedException(nameof(PersistentGpuTextures));
        var pitch = checked(Width * 4);
        if (bgraPixels.Length != checked(pitch * Height))
            throw new ArgumentException("Unexpected BGRA pixel count.", nameof(bgraPixels));

        var next = (activeIndex + 1) & 1;
        var resource = resources[next];
        var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Vtable(context)[14];
        MappedSubresource mapped = default;
        // DO_NOT_WAIT (0x100000) is illegal with WRITE_DISCARD (D3D11_MAP=4).
        // See D3D11_MAP_FLAG in Microsoft Direct3D 11 documentation.
        var hr = map(context, resource, 0, D3D11MapWriteDiscard, 0, &mapped);
        if (hr == DxgiErrorWasStillDrawing) return false;
        if (hr < 0) throw new InvalidOperationException($"D3D11 Map failed (HRESULT 0x{hr:X8}).");
        if (mapped.Data == null)
        {
            Unmap(resource);
            throw new InvalidOperationException("D3D11 Map returned a null pixel buffer.");
        }

        try
        {
            if (mapped.RowPitch < (uint)pitch)
                throw new InvalidOperationException("D3D11 returned an invalid texture row pitch.");
            fixed (byte* source = bgraPixels)
            {
                for (var y = 0; y < Height; y++)
                {
                    var dst = (byte*)mapped.Data + (nint)((long)y * mapped.RowPitch);
                    Buffer.MemoryCopy(source + (nint)((long)y * pitch), dst,
                        mapped.RowPitch, pitch);
                }
            }
        }
        finally { Unmap(resource); }
        activeIndex = next;
        return true;
    }

    private void Unmap(nint resource)
    {
        var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Vtable(context)[15];
        unmap(context, resource, 0);
    }

    private static nint GetResourceFromSrv(nint srv)
    {
        nint resource = 0;
        var getResource = (delegate* unmanaged[Stdcall]<nint, nint*, void>)Vtable(srv)[7];
        getResource(srv, &resource);
        return resource; // ID3D11ShaderResourceView::GetResource AddRefs result.
    }

    private static nint* Vtable(nint ptr) => *(nint**)ptr;

    private static void Release(nint ptr)
    {
        if (ptr == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Vtable(ptr)[2];
        _ = release(ptr);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Release(context);
        context = 0;
        for (var i = 0; i < resources.Length; i++)
        {
            wraps[i]?.Dispose();
            wraps[i] = null;
            Release(resources[i]);
            resources[i] = 0;
        }
        activeIndex = -1;
    }

    private sealed class NativeSrvWrap : IDalamudTextureWrap
    {
        private nint srv;
        public int Width { get; }
        public int Height { get; }
        public Vector2 Size => new(Width, Height);
        public ImTextureID Handle
        {
            get
            {
                if (srv == 0) throw new ObjectDisposedException(nameof(NativeSrvWrap));
                var pointer = srv;
                return Unsafe.As<nint, ImTextureID>(ref pointer);
            }
        }

        public NativeSrvWrap(nint srv, int width, int height)
        {
            this.srv = srv;
            Width = width;
            Height = height;
        }

        public IDalamudTextureWrap CreateWrapSharingLowLevelResource()
        {
            if (srv == 0) throw new ObjectDisposedException(nameof(NativeSrvWrap));
            var addRef = (delegate* unmanaged[Stdcall]<nint, uint>)Vtable(srv)[1];
            _ = addRef(srv);
            return new NativeSrvWrap(srv, Width, Height);
        }

        public void Dispose()
        {
            var old = srv;
            srv = 0;
            Release(old);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
