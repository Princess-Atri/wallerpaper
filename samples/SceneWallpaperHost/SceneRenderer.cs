using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace SceneWallpaperHost;

internal sealed class SceneRenderer : IDisposable
{
    private readonly Action<string> _log;
    private readonly List<RenderLayer> _layers = new();
    private readonly Dictionary<string, IntPtr> _textureCache = new(StringComparer.OrdinalIgnoreCase);

    private unsafe ID3D11Device* _device;
    private unsafe ID3D11DeviceContext* _context;
    private unsafe IDXGISwapChain* _swapChain;
    private unsafe IDXGIAdapter3* _adapter3;
    private unsafe ID3D11RenderTargetView* _rtv;
    private unsafe ID3D11RenderTargetView* _backRtv;
    private unsafe ID3D11Texture2D* _sceneTexture;
    private unsafe ID3D11RenderTargetView* _sceneRtv;
    public float ShaderIntensity { get; set; } = 1.0f;

    private unsafe ID3D11ShaderResourceView* _sceneSrv;
    private unsafe ID3D11PixelShader* _blitPs;
    private float _renderScale = 1.0f;
    private int _renderWidth;
    private int _renderHeight;
    private unsafe ID3D11VertexShader* _vs;
    private unsafe ID3D11PixelShader* _colorPs;
    private unsafe ID3D11PixelShader* _imagePs;
    private unsafe ID3D11Buffer* _sceneCb;
    private unsafe ID3D11Buffer* _layerCb;
    private unsafe ID3D11Buffer* _blitCb;
    private unsafe ID3D11Query* _gpuDisjoint;
    private unsafe ID3D11Query* _gpuStart;
    private unsafe ID3D11Query* _gpuEnd;
    private bool _gpuQueryPending;
    public double LastGpuMs { get; private set; }
    private unsafe ID3D11SamplerState* _sampler;
    private unsafe ID3D11BlendState* _blend;
    private unsafe ID3D11RasterizerState* _raster;
    private unsafe ID3D11VertexShader* _particleVs;
    private unsafe ID3D11PixelShader* _particlePs;
    private unsafe ID3D11InputLayout* _particleLayout;
    private unsafe ID3D11Buffer* _particleVb;
    private unsafe ID3D11Buffer* _particleViewCb;
    private int _particleMaxVertices;
    private int _width;
    private int _height;
    private bool _disposed;
    public double LastDrawMs { get; private set; }
    public double LastPresentMs { get; private set; }
    public float RenderScale => _renderScale;
    public int RenderWidth => _renderWidth;
    public int RenderHeight => _renderHeight;
    public int ScreenWidth => _width;
    public int ScreenHeight => _height;
    public ulong VramUsedBytes { get; private set; }
    public ulong VramBudgetBytes { get; private set; }

    public unsafe void UpdateVramInfo()
    {
        if (_adapter3 is null)
            return;
        {
            QueryVideoMemoryInfo info;
            if (_adapter3->QueryVideoMemoryInfo(0, MemorySegmentGroup.Local, &info) >= 0)
            {
                VramUsedBytes = info.CurrentUsage;
                VramBudgetBytes = info.Budget;
            }
        }
    }
    public int CulledLayers { get; private set; }

    public int ActiveParticles
    {
        get
        {
            var total = 0;
            foreach (var layer in _layers)
                total += layer.Particles?.Count ?? 0;
            return total;
        }
    }

    public float ActiveParticleSpeed
    {
        get
        {
            foreach (var layer in _layers)
                if (layer.Particles is not null)
                    return layer.ParticleSpeed;
            return 0;
        }
    }

    private const int SceneCbSize = 80;
    private const int LayerCbSize = 64;

    public SceneRenderer(Action<string> log)
    {
        _log = log;
    }

    public unsafe int Width => _width;
    public unsafe int Height => _height;

    public unsafe void Initialize(IntPtr hwnd, SceneConfig config, float renderScale = 1.0f)
    {
#pragma warning disable CS0618 // no Silk window: we attach the swap chain to a raw WorkerW HWND
        var api = D3D11.GetApi();
#pragma warning restore CS0618
        var desc = new SwapChainDesc
        {
            BufferCount = 2,
            BufferDesc = new ModeDesc
            {
                Width = 1920,
                Height = 1080,
                Format = Format.FormatR8G8B8A8Unorm,
                RefreshRate = new Rational(60, 1)
            },
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            BufferUsage = 0x20, // DXGI_USAGE_RENDER_TARGET_OUTPUT
            OutputWindow = hwnd,
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };

        GetClientSize(hwnd, out _width, out _height);
        if (_width <= 0 || _height <= 0)
            throw new InvalidOperationException("invalid host client size");
        desc.BufferDesc.Width = (uint)_width;
        desc.BufferDesc.Height = (uint)_height;

        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        IDXGISwapChain* swap = null;
        D3DFeatureLevel level = 0;

        var hr = api.CreateDeviceAndSwapChain(
            null, D3DDriverType.Hardware, IntPtr.Zero, 0,
            null, 0, D3D11.SdkVersion, ref desc,
            &swap, &device, &level, &context);

        if (hr < 0)
            throw new InvalidOperationException($"D3D11CreateDeviceAndSwapChain failed: 0x{hr:X8}");

        _device = device;
        _context = context;
        _swapChain = swap;

        CreateVramMonitor();
        CreateBackBuffer();
        CreateRenderTargets(renderScale);
        CreatePipeline();
        CreateGpuQueries();
        CreateConstantBuffers();
        CreateSamplerAndBlend();
        CreateParticlePipeline(config.MaxParticles);
        LoadLayers(config);

        _log($"D3D11 initialized: {_width}x{_height}, featureLevel={level}, layers={_layers.Count}");
    }

    public unsafe void Render(
        double time,
        double dt,
        double mouseX,
        double mouseY,
        AudioFrame audio,
        int fpsCap,
        string? capturePath = null,
        bool vsync = false)
    {
        if (_disposed || _context is null || _rtv is null || _swapChain is null)
            return;

        var frameTimer = Stopwatch.StartNew();
        ReadGpuQuery();
        var gpuQueryIssued = false;
        if (_gpuDisjoint is not null && _gpuStart is not null && _gpuEnd is not null && !_gpuQueryPending)
        {
            _context->Begin((ID3D11Asynchronous*)_gpuDisjoint);
            _context->End((ID3D11Asynchronous*)_gpuStart);
            gpuQueryIssued = true;
        }
        var renderMouseX = mouseX * _renderWidth / Math.Max(1.0, _width);
        var renderMouseY = mouseY * _renderHeight / Math.Max(1.0, _height);
        UpdateSceneConstants(time, renderMouseX, renderMouseY, audio);

        var clear = new float[] { 0.02f, 0.025f, 0.04f, 1.0f };
        fixed (float* pClear = clear)
            _context->ClearRenderTargetView(_rtv, pClear);

        var rtv = _rtv;
        var sceneCb = _sceneCb;
        var layerCb = _layerCb;
        var sampler = _sampler;
        var blend = _blend;
        var vs = _vs;
        var raster = _raster;
        CulledLayers = 0;

        _context->OMSetRenderTargets(1, &rtv, null);
        var viewport = new Viewport { TopLeftX = 0, TopLeftY = 0, Width = _renderWidth, Height = _renderHeight, MinDepth = 0, MaxDepth = 1 };
        _context->RSSetViewports(1, &viewport);
        _context->RSSetState(raster);
        _context->IASetInputLayout(null);
        _context->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        _context->VSSetShader(vs, null, 0);
        _context->PSSetConstantBuffers(0, 1, &sceneCb);
        _context->PSSetConstantBuffers(1, 1, &layerCb);
        _context->PSSetSamplers(0, 1, &sampler);
        _context->OMSetBlendState(blend, null, 0xffffffff);

        foreach (var layer in _layers)
        {
            if (!layer.Visible || layer.Opacity <= 0.001f)
                continue;
            if (!IsLayerVisible(layer))
            {
                CulledLayers++;
                continue;
            }

            if (layer.Particles is not null)
            {
                DrawParticles(layer.Particles, time, dt, audio);
                continue;
            }

            _context->IASetInputLayout(null);
            _context->VSSetShader(_vs, null, 0);
            UpdateLayerConstants(layer);
            var ps = layer.PixelShader is not null ? layer.PixelShader : _colorPs;
            _context->PSSetShader(ps, null, 0);
            if (layer.Srv is not null)
            {
                var srv = layer.Srv;
                _context->PSSetShaderResources(0, 1, &srv);
            }
            if (layer.Textures.Count > 0)
            {
                var ptrs = layer.Textures.ToArray();
                var samplerArray = new ID3D11SamplerState*[ptrs.Length];
                for (var ti = 0; ti < samplerArray.Length; ti++)
                    samplerArray[ti] = _sampler;
                fixed (IntPtr* pTex = ptrs)
                fixed (ID3D11SamplerState** pSamp = samplerArray)
                {
                    _context->PSSetShaderResources(0, (uint)ptrs.Length, (ID3D11ShaderResourceView**)pTex);
                    _context->PSSetSamplers(0, (uint)samplerArray.Length, pSamp);
                }
            }
            _context->Draw(3, 0);
        }

        if (_sceneSrv is not null && _backRtv is not null && _blitPs is not null)
        {
            var backRtv = _backRtv;
            var sceneSrv = _sceneSrv;
            var blitPs = _blitPs;
            var sample = _sampler;
            var blitParams = new BlitParams { P0 = new Vector4(_width, _height, 0, 0) };
            UpdateBuffer(_blitCb, &blitParams, 16);
            var blitCb = _blitCb;
            _context->OMSetRenderTargets(1, &backRtv, null);
            var backViewport = new Viewport { TopLeftX = 0, TopLeftY = 0, Width = _width, Height = _height, MinDepth = 0, MaxDepth = 1 };
            _context->RSSetViewports(1, &backViewport);
            _context->IASetInputLayout(null);
            _context->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
            _context->VSSetShader(_vs, null, 0);
            _context->PSSetShader(blitPs, null, 0);
            _context->PSSetConstantBuffers(3, 1, &blitCb);
            _context->PSSetSamplers(0, 1, &sample);
            _context->PSSetShaderResources(0, 1, &sceneSrv);
            _context->Draw(3, 0);
        }

        if (gpuQueryIssued)
        {
            _context->End((ID3D11Asynchronous*)_gpuEnd);
            _context->End((ID3D11Asynchronous*)_gpuDisjoint);
            _gpuQueryPending = true;
        }

        if (!string.IsNullOrWhiteSpace(capturePath))
            CapturePng(capturePath);

        LastDrawMs = frameTimer.Elapsed.TotalMilliseconds;
        // No vsync: the host owns the frame-rate limit. Present(1,0) can block
        // for multiple vblanks on some WorkerW/DWM configurations.
        var presentHr = _swapChain->Present(vsync ? 1u : 0u, 0);
        LastPresentMs = frameTimer.Elapsed.TotalMilliseconds - LastDrawMs;
        if (presentHr < 0)
            _log($"Present failed: 0x{presentHr:X8}");
    }

    /// <summary>Test/diagnostic: copy the current back buffer to a PNG.</summary>
    public unsafe void CapturePng(string path)
    {
        if (_swapChain is null || _device is null || _context is null)
            return;

        ID3D11Texture2D* back = null;
        var iid = typeof(ID3D11Texture2D).GUID;
        var hr = _swapChain->GetBuffer(0, ref iid, (void**)&back);
        if (hr < 0)
            throw new InvalidOperationException($"GetBuffer(capture): 0x{hr:X8}");

        ID3D11Texture2D* staging = null;
        try
        {
            var desc = new Texture2DDesc
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.FormatR8G8B8A8Unorm,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = Usage.Staging,
                BindFlags = 0,
                CPUAccessFlags = (uint)CpuAccessFlag.Read,
                MiscFlags = 0
            };
            hr = _device->CreateTexture2D(&desc, null, &staging);
            if (hr < 0) throw new InvalidOperationException($"CreateTexture2D(staging): 0x{hr:X8}");
            _context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)back);

            MappedSubresource mapped;
            hr = _context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped);
            if (hr < 0) throw new InvalidOperationException($"Map(staging): 0x{hr:X8}");
            try
            {
                using var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    // Back buffer is R8G8B8A8; System.Drawing expects BGRA.
                    for (var y = 0; y < _height; y++)
                    {
                        var src = (byte*)mapped.PData + y * mapped.RowPitch;
                        var dst = (byte*)data.Scan0 + y * data.Stride;
                        for (var x = 0; x < _width; x++)
                        {
                            var s = x * 4;
                            dst[s + 0] = src[s + 2];
                            dst[s + 1] = src[s + 1];
                            dst[s + 2] = src[s + 0];
                            dst[s + 3] = src[s + 3];
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                bmp.Save(path, ImageFormat.Png);
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            Release(staging);
            Release(back);
        }
    }

    public unsafe void ApplyScriptOps(IReadOnlyList<ScriptOp> ops)
    {
        foreach (var op in ops)
        {
            var layer = _layers.Find(l => string.Equals(l.Name, op.Layer, StringComparison.OrdinalIgnoreCase));
            if (layer is null)
                continue;

            switch (op.Op)
            {
                case "position":
                    if (op.X.HasValue && op.Y.HasValue)
                    {
                        layer.PositionX = op.X.Value;
                        layer.PositionY = op.Y.Value;
                    }
                    break;
                case "opacity":
                    if (op.Value.HasValue)
                        layer.Opacity = Math.Clamp(op.Value.Value, 0, 1);
                    break;
                case "scale":
                    if (op.X.HasValue && op.Y.HasValue)
                    {
                        layer.ScaleX = Math.Max(0.001f, op.X.Value);
                        layer.ScaleY = Math.Max(0.001f, op.Y.Value);
                    }
                    break;
                case "rotation":
                    if (op.Value.HasValue)
                        layer.Rotation = op.Value.Value;
                    break;
                case "particleCount":
                    if (op.Value.HasValue)
                        layer.RequestedParticleCount = (int)Math.Clamp(op.Value.Value, 0, ParticleLayer.MaxHardwareParticles);
                    break;
                case "particleSpeed":
                    if (op.Value.HasValue)
                        layer.ParticleSpeed = Math.Clamp(op.Value.Value, 0, 20);
                    break;
            }
        }
    }

    private bool IsLayerVisible(RenderLayer layer)
    {
        var cx = layer.PositionX * _renderWidth;
        var cy = layer.PositionY * _renderHeight;
        var hw = layer.SizeX * _renderWidth * layer.ScaleX * 0.5f;
        var hh = layer.SizeY * _renderHeight * layer.ScaleY * 0.5f;
        var c = MathF.Abs(MathF.Cos(layer.Rotation));
        var s = MathF.Abs(MathF.Sin(layer.Rotation));
        var ex = c * hw + s * hh;
        var ey = s * hw + c * hh;
        return cx + ex >= 0 && cx - ex <= _renderWidth &&
               cy + ey >= 0 && cy - ey <= _renderHeight;
    }

    private unsafe void DrawParticles(ParticleLayer particles, double time, double dt, AudioFrame audio)
    {
        particles.Visible = true;
        var owner = _layers.Find(l => ReferenceEquals(l.Particles, particles));
        if (owner is not null)
        {
            particles.PositionX = owner.PositionX;
            particles.PositionY = owner.PositionY;
            particles.SizeX = Math.Max(0.001f, owner.SizeX);
            particles.SizeY = Math.Max(0.001f, owner.SizeY);
            particles.ScaleX = Math.Max(0.001f, owner.ScaleX);
            particles.ScaleY = Math.Max(0.001f, owner.ScaleY);
            particles.Rotation = owner.Rotation;
            particles.Opacity = owner.Opacity;
            particles.SpeedScale = owner.ParticleSpeed;
            if (owner.RequestedParticleCount.HasValue)
            {
                particles.SetCount(owner.RequestedParticleCount.Value);
                owner.RequestedParticleCount = null;
            }
        }

        particles.Update(time, dt, audio);
        var count = particles.BuildVertices(_renderWidth, _renderHeight, audio);
        if (count <= 0 || _particleVb is null || _particleLayout is null)
            return;

        count = Math.Min(count, _particleMaxVertices);
        var mapped = default(MappedSubresource);
        var hr = _context->Map((ID3D11Resource*)_particleVb, 0, Map.WriteDiscard, 0, &mapped);
        if (hr < 0)
        {
            _log($"particle Map failed: 0x{hr:X8}");
            return;
        }

        try
        {
            fixed (ParticleVertex* p = particles.Vertices)
                Buffer.MemoryCopy(p, (void*)mapped.PData, count * ParticleLayer.VertexStride, count * ParticleLayer.VertexStride);
        }
        finally
        {
            _context->Unmap((ID3D11Resource*)_particleVb, 0);
        }

        var view = new ParticleView { P0 = new Vector4(_renderWidth, _renderHeight, 0, 0) };
        UpdateBuffer(_particleViewCb, &view, 16);

        var vb = _particleVb;
        var stride = (uint)ParticleLayer.VertexStride;
        uint offset = 0;
        var viewCb = _particleViewCb;
        _context->IASetInputLayout(_particleLayout);
        _context->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        _context->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        _context->VSSetShader(_particleVs, null, 0);
        _context->VSSetConstantBuffers(2, 1, &viewCb);
        _context->PSSetShader(_particlePs, null, 0);
        _context->Draw((uint)count, 0);
    }

    private unsafe void CreateVramMonitor()
    {
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIAdapter3* adapter3 = null;
        var iidDevice = typeof(IDXGIDevice).GUID;
        var iidAdapter3 = typeof(IDXGIAdapter3).GUID;
        try
        {
            if (((IUnknown*)_device)->QueryInterface(ref iidDevice, (void**)&dxgiDevice) < 0)
                return;
            if (dxgiDevice->GetAdapter(&adapter) < 0)
                return;
            if (((IUnknown*)adapter)->QueryInterface(ref iidAdapter3, (void**)&adapter3) < 0)
                return;
            _adapter3 = adapter3;
            UpdateVramInfo();
            _log($"VRAM monitor: used={VramUsedBytes / 1048576.0:0.0}MB budget={VramBudgetBytes / 1048576.0:0.0}MB");
        }
        finally
        {
            Release(adapter);
            Release(dxgiDevice);
        }
    }

    private unsafe void CreateBackBuffer()
    {
        ID3D11Texture2D* backBuffer = null;
        var iid = typeof(ID3D11Texture2D).GUID;
        var hr = _swapChain->GetBuffer(0, ref iid, (void**)&backBuffer);
        if (hr < 0)
            throw new InvalidOperationException($"GetBuffer failed: 0x{hr:X8}");

        ID3D11RenderTargetView* rtv = null;
        hr = _device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, &rtv);
        _backRtv = rtv;
        Release(backBuffer);
        if (hr < 0)
            throw new InvalidOperationException($"CreateRenderTargetView failed: 0x{hr:X8}");
    }

    private unsafe void CreateRenderTargets(float scale)
    {
        _renderScale = Math.Clamp(scale, 0.25f, 4.0f);
        _renderWidth = Math.Max(1, (int)Math.Round(_width * _renderScale));
        _renderHeight = Math.Max(1, (int)Math.Round(_height * _renderScale));

        if (Math.Abs(_renderScale - 1.0f) < 0.001f)
        {
            _rtv = _backRtv;
            return;
        }

        var desc = new Texture2DDesc
        {
            Width = (uint)_renderWidth,
            Height = (uint)_renderHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
            CPUAccessFlags = 0,
            MiscFlags = 0
        };

        ID3D11Texture2D* tex = null;
        ID3D11RenderTargetView* rtv = null;
        ID3D11ShaderResourceView* srv = null;
        var hr = _device->CreateTexture2D(&desc, null, &tex);
        if (hr >= 0) hr = _device->CreateRenderTargetView((ID3D11Resource*)tex, null, &rtv);
        if (hr >= 0) hr = _device->CreateShaderResourceView((ID3D11Resource*)tex, null, &srv);
        if (hr < 0)
        {
            Release(srv);
            Release(rtv);
            Release(tex);
            throw new InvalidOperationException($"CreateRenderTargets: 0x{hr:X8}");
        }

        _sceneTexture = tex;
        _sceneRtv = rtv;
        _sceneSrv = srv;
        _rtv = _sceneRtv;
        _log($"render target: {_renderWidth}x{_renderHeight} (scale {_renderScale:0.##}x)");
    }

    public void ResizeToClient(IntPtr hwnd)
    {
        GetClientSize(hwnd, out var w, out var h);
        Resize(w, h);
    }

    public unsafe void SetRenderScale(float scale)
    {
        scale = Math.Clamp(scale, 0.25f, 4.0f);
        if (Math.Abs(scale - _renderScale) < 0.001f)
            return;
        ReleaseRenderTargets();
        CreateRenderTargets(scale);
        _log($"render scale changed to {_renderScale:0.##}x ({_renderWidth}x{_renderHeight})");
    }

    public unsafe void Resize(int width, int height)
    {
        if (_disposed || _swapChain is null || width <= 0 || height <= 0)
            return;
        if (width == _width && height == _height)
            return;

        _context->OMSetRenderTargets(0, null, null);
        ReleaseRenderTargets();
        if (_backRtv is not null)
        {
            Release(_backRtv);
            _backRtv = null;
        }

        _width = width;
        _height = height;
        var hr = _swapChain->ResizeBuffers(2, (uint)width, (uint)height, Format.FormatR8G8B8A8Unorm, 0);
        if (hr < 0)
        {
            _log($"ResizeBuffers failed: 0x{hr:X8}");
            return;
        }

        CreateBackBuffer();
        CreateRenderTargets(_renderScale);
        _log($"resized: {_width}x{_height}, render {_renderWidth}x{_renderHeight}");
    }

    private unsafe void ReleaseRenderTargets()
    {
        Release(_sceneSrv);
        Release(_sceneRtv);
        Release(_sceneTexture);
        _sceneSrv = null;
        _sceneRtv = null;
        _sceneTexture = null;
        _rtv = null;
    }

    private unsafe void CreatePipeline()
    {
        var vsCode = ShaderCompiler.Compile(VertexShader, "main", "vs_5_0");
        var colorCode = ShaderCompiler.Compile(ColorShader, "main", "ps_5_0");
        var imageCode = ShaderCompiler.Compile(ImageShader, "main", "ps_5_0");
        var blitCode = ShaderCompiler.Compile(BlitShader, "main", "ps_5_0");

        fixed (byte* p = vsCode)
        {
            ID3D11VertexShader* vs = null;
            var hr = _device->CreateVertexShader(p, (nuint)vsCode.Length, null, &vs);
            if (hr < 0) throw new InvalidOperationException($"CreateVertexShader: 0x{hr:X8}");
            _vs = vs;
        }
        fixed (byte* p = colorCode)
        {
            ID3D11PixelShader* ps = null;
            var hr = _device->CreatePixelShader(p, (nuint)colorCode.Length, null, &ps);
            if (hr < 0) throw new InvalidOperationException($"CreatePixelShader(color): 0x{hr:X8}");
            _colorPs = ps;
        }
        fixed (byte* p = imageCode)
        {
            ID3D11PixelShader* ps = null;
            var hr = _device->CreatePixelShader(p, (nuint)imageCode.Length, null, &ps);
            if (hr < 0) throw new InvalidOperationException($"CreatePixelShader(image): 0x{hr:X8}");
            _imagePs = ps;
        }
        fixed (byte* p = blitCode)
        {
            ID3D11PixelShader* ps = null;
            var hr = _device->CreatePixelShader(p, (nuint)blitCode.Length, null, &ps);
            if (hr < 0) throw new InvalidOperationException($"CreatePixelShader(blit): 0x{hr:X8}");
            _blitPs = ps;
        }
    }

    private unsafe void CreateParticlePipeline(int maxParticles)
    {
        var vsCode = ShaderCompiler.Compile(ParticleVertexShader, "main", "vs_5_0");
        var psCode = ShaderCompiler.Compile(ParticlePixelShader, "main", "ps_5_0");

        fixed (byte* p = vsCode)
        {
            ID3D11VertexShader* vs = null;
            var hr = _device->CreateVertexShader(p, (nuint)vsCode.Length, null, &vs);
            if (hr < 0) throw new InvalidOperationException($"CreateParticleVS: 0x{hr:X8}");
            _particleVs = vs;
        }
        fixed (byte* p = psCode)
        {
            ID3D11PixelShader* ps = null;
            var hr = _device->CreatePixelShader(p, (nuint)psCode.Length, null, &ps);
            if (hr < 0) throw new InvalidOperationException($"CreateParticlePS: 0x{hr:X8}");
            _particlePs = ps;
        }

        var posName = Marshal.StringToHGlobalAnsi("POSITION");
        var uvName = Marshal.StringToHGlobalAnsi("TEXCOORD");
        var colorName = Marshal.StringToHGlobalAnsi("COLOR");
        try
        {
            var elements = new InputElementDesc[]
            {
                new()
                {
                    SemanticName = (byte*)posName,
                    SemanticIndex = 0,
                    Format = Format.FormatR32G32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 0,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0
                },
                new()
                {
                    SemanticName = (byte*)uvName,
                    SemanticIndex = 0,
                    Format = Format.FormatR32G32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 8,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0
                },
                new()
                {
                    SemanticName = (byte*)colorName,
                    SemanticIndex = 0,
                    Format = Format.FormatR32G32B32A32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 16,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0
                }
            };

            fixed (InputElementDesc* pElements = elements)
            fixed (byte* pVs = vsCode)
            {
                ID3D11InputLayout* layout = null;
                var hr = _device->CreateInputLayout(
                    pElements, (uint)elements.Length, pVs, (nuint)vsCode.Length, &layout);
                if (hr < 0) throw new InvalidOperationException($"CreateParticleInputLayout: 0x{hr:X8}");
                _particleLayout = layout;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(posName);
            Marshal.FreeHGlobal(uvName);
            Marshal.FreeHGlobal(colorName);
        }

        var viewDesc = new BufferDesc
        {
            ByteWidth = 16,
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write
        };
        ID3D11Buffer* viewCb = null;
        var viewHr = _device->CreateBuffer(&viewDesc, null, &viewCb);
        _particleViewCb = viewCb;
        if (viewHr < 0) throw new InvalidOperationException($"CreateParticleCB: 0x{viewHr:X8}");

        _particleMaxVertices = Math.Clamp(maxParticles, 1, ParticleLayer.MaxHardwareParticles) * ParticleLayer.VerticesPerParticle;
        var vbDesc = new BufferDesc
        {
            ByteWidth = (uint)(_particleMaxVertices * ParticleLayer.VertexStride),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write
        };
        ID3D11Buffer* vb = null;
        var vbHr = _device->CreateBuffer(&vbDesc, null, &vb);
        _particleVb = vb;
        if (vbHr < 0) throw new InvalidOperationException($"CreateParticleVB: 0x{vbHr:X8}");
    }

    private unsafe void CreateGpuQueries()
    {
        var disjointDesc = new QueryDesc { Query = Query.TimestampDisjoint, MiscFlags = 0 };
        var timestampDesc = new QueryDesc { Query = Query.Timestamp, MiscFlags = 0 };
        ID3D11Query* disjoint = null;
        ID3D11Query* start = null;
        ID3D11Query* end = null;
        var hr = _device->CreateQuery(&disjointDesc, &disjoint);
        if (hr >= 0) hr = _device->CreateQuery(&timestampDesc, &start);
        if (hr >= 0) hr = _device->CreateQuery(&timestampDesc, &end);
        _gpuDisjoint = disjoint;
        _gpuStart = start;
        _gpuEnd = end;
        if (hr < 0)
            _log($"GPU timestamp queries unavailable: 0x{hr:X8}");
    }

    private unsafe void ReadGpuQuery()
    {
        if (!_gpuQueryPending || _gpuDisjoint is null || _gpuStart is null || _gpuEnd is null)
            return;

        QueryDataTimestampDisjoint disjoint;
        ulong start;
        ulong end;
        var hr1 = _context->GetData((ID3D11Asynchronous*)_gpuDisjoint, &disjoint, (uint)sizeof(QueryDataTimestampDisjoint), 0);
        var hr2 = _context->GetData((ID3D11Asynchronous*)_gpuStart, &start, sizeof(ulong), 0);
        var hr3 = _context->GetData((ID3D11Asynchronous*)_gpuEnd, &end, sizeof(ulong), 0);
        if (hr1 == 0 && hr2 == 0 && hr3 == 0)
        {
            _gpuQueryPending = false;
            if (!disjoint.Disjoint && disjoint.Frequency > 0 && end >= start)
                LastGpuMs = (end - start) * 1000.0 / disjoint.Frequency;
        }
    }

    private unsafe void CreateConstantBuffers()
    {
        var sceneDesc = new BufferDesc
        {
            ByteWidth = SceneCbSize,
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write
        };
        ID3D11Buffer* sceneCb = null;
        var hr = _device->CreateBuffer(&sceneDesc, null, &sceneCb);
        _sceneCb = sceneCb;
        if (hr < 0) throw new InvalidOperationException($"CreateBuffer(scene): 0x{hr:X8}");

        var layerDesc = sceneDesc;
        layerDesc.ByteWidth = LayerCbSize;
        ID3D11Buffer* layerCb = null;
        hr = _device->CreateBuffer(&layerDesc, null, &layerCb);
        _layerCb = layerCb;
        if (hr < 0) throw new InvalidOperationException($"CreateBuffer(layer): 0x{hr:X8}");

        var blitDesc = sceneDesc;
        blitDesc.ByteWidth = 16;
        ID3D11Buffer* blitCb = null;
        hr = _device->CreateBuffer(&blitDesc, null, &blitCb);
        _blitCb = blitCb;
        if (hr < 0) throw new InvalidOperationException($"CreateBuffer(blit): 0x{hr:X8}");
    }

    private unsafe void CreateSamplerAndBlend()
    {
        var samplerDesc = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunc.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        };
        ID3D11SamplerState* sampler = null;
        var hr = _device->CreateSamplerState(&samplerDesc, &sampler);
        _sampler = sampler;
        if (hr < 0) throw new InvalidOperationException($"CreateSamplerState: 0x{hr:X8}");

        var blendDesc = new BlendDesc
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
            RenderTarget = new BlendDesc.RenderTargetBuffer
            {
                Element0 = new RenderTargetBlendDesc
                {
                    BlendEnable = true,
                    SrcBlend = Blend.SrcAlpha,
                    DestBlend = Blend.InvSrcAlpha,
                    BlendOp = BlendOp.Add,
                    SrcBlendAlpha = Blend.One,
                    DestBlendAlpha = Blend.InvSrcAlpha,
                    BlendOpAlpha = BlendOp.Add,
                    RenderTargetWriteMask = (byte)ColorWriteEnable.All
                }
            }
        };
        ID3D11BlendState* blend = null;
        hr = _device->CreateBlendState(&blendDesc, &blend);
        _blend = blend;
        if (hr < 0) throw new InvalidOperationException($"CreateBlendState: 0x{hr:X8}");

        // The full-screen triangle is intentionally winding-agnostic; disable
        // culling so D3D's default front-face convention cannot remove it.
        var rasterDesc = new RasterizerDesc
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
            DepthBias = 0,
            DepthBiasClamp = 0,
            SlopeScaledDepthBias = 0
        };
        ID3D11RasterizerState* raster = null;
        hr = _device->CreateRasterizerState(&rasterDesc, &raster);
        _raster = raster;
        if (hr < 0) throw new InvalidOperationException($"CreateRasterizerState: 0x{hr:X8}");
    }

    private unsafe void LoadLayers(SceneConfig config)
    {
        var i = 0;
        foreach (var cfg in config.Layers)
        {
            LayerConfig.Normalize(cfg, $"layer{i}");
            var layer = new RenderLayer
            {
                Name = cfg.Name,
                Type = cfg.Type,
                PositionX = cfg.Position[0],
                PositionY = cfg.Position[1],
                SizeX = Math.Max(0.001f, cfg.Size[0]),
                SizeY = Math.Max(0.001f, cfg.Size[1]),
                ScaleX = Math.Max(0.001f, cfg.Scale[0]),
                ScaleY = Math.Max(0.001f, cfg.Scale[1]),
                Rotation = cfg.Rotation,
                Opacity = cfg.Opacity,
                Visible = cfg.Visible,
                Tint = cfg.Color.Length >= 4
                    ? new Vector4(cfg.Color[0], cfg.Color[1], cfg.Color[2], cfg.Color[3])
                    : new Vector4(1, 1, 1, 1)
            };

            if (cfg.Type.Equals("particles", StringComparison.OrdinalIgnoreCase))
            {
                layer.Particles = new ParticleLayer(cfg, config.MaxParticles);
                _log($"particle layer '{cfg.Name}' count={layer.Particles.Count} (max={config.MaxParticles})");
            }
            else if (cfg.Type.Equals("shader", StringComparison.OrdinalIgnoreCase))
            {
                var path = config.ResolvePath(cfg.Shader);
                var glsl = File.ReadAllText(path);
                var hlsl = GlslTranslator.Translate(glsl, out var samplers);
                if (samplers.Count > 0)
                {
                    for (var ti = 0; ti < samplers.Count; ti++)
                    {
                        if (ti >= cfg.Textures.Count)
                            throw new InvalidDataException(
                                $"shader '{cfg.Name}' samples '{samplers[ti]}' but layer.textures has only {cfg.Textures.Count} entries");
                        var texPath = config.ResolvePath(cfg.Textures[ti]);
                        if (_textureCache.TryGetValue(texPath, out var cached))
                        {
                            layer.Textures.Add(cached);
                        }
                        else
                        {
                            var srv = LoadTexture(texPath, config.MaxTextureSize, config.TextureCompression);
                            var ptr = (IntPtr)srv;
                            _textureCache[texPath] = ptr;
                            layer.Textures.Add(ptr);
                        }
                        _log($"shader layer '{cfg.Name}' sampler t{ti} <- {cfg.Textures[ti]}");
                    }
                }
                File.WriteAllText(path + ".hlsl.generated", hlsl);
                var bytecode = ShaderCompiler.Compile(hlsl, "main", "ps_5_0");
                fixed (byte* p = bytecode)
                {
                    ID3D11PixelShader* ps = null;
                    var hr = _device->CreatePixelShader(p, (nuint)bytecode.Length, null, &ps);
                    if (hr < 0) throw new InvalidOperationException($"CreatePixelShader({cfg.Name}): 0x{hr:X8}");
                    layer.PixelShader = ps;
                }
                _log($"shader layer '{cfg.Name}' <- {path}");
            }
            else if (cfg.Type.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                var imagePath = config.ResolvePath(cfg.Image);
                layer.PixelShader = _imagePs;
                if (_textureCache.TryGetValue(imagePath, out var cached))
                {
                    layer.Srv = (ID3D11ShaderResourceView*)cached;
                    _log($"image layer '{cfg.Name}' <- {cfg.Image} (shared texture)");
                }
                else
                {
                    layer.Srv = LoadTexture(imagePath, config.MaxTextureSize, config.TextureCompression);
                    _textureCache[imagePath] = (IntPtr)layer.Srv;
                    _log($"image layer '{cfg.Name}' <- {cfg.Image}");
                }
            }
            else
            {
                layer.PixelShader = _colorPs;
            }

            _layers.Add(layer);
            i++;
        }
    }

    private unsafe ID3D11ShaderResourceView* LoadTexture(string path, int maxSize, string compression)
    {
        using var bitmap = new Bitmap(path);
        var w = Math.Min(bitmap.Width, maxSize);
        var h = Math.Min(bitmap.Height, maxSize);

        using var resized = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, 0, 0, w, h);
        }

        var data = resized.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var useBc7 = compression == "bc7";
            var useBc3 = !useBc7 && (compression == "bc3" || (compression == "auto" && (long)w * h >= 512 * 512));
            if (useBc3 || useBc7)
            {
                var rgba = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                {
                    var src = (byte*)data.Scan0 + y * data.Stride;
                    for (var x = 0; x < w; x++)
                    {
                        var s = x * 4;
                        var d = (y * w + x) * 4;
                        rgba[d + 0] = src[s + 2];
                        rgba[d + 1] = src[s + 1];
                        rgba[d + 2] = src[s + 0];
                        rgba[d + 3] = src[s + 3];
                    }
                }

                var blocks = useBc7
                    ? TextureCompressor.EncodeBc7(rgba, w, h)
                    : TextureCompressor.EncodeBc3(rgba, w, h);
                var blockPitch = (uint)(((w + 3) / 4) * 16);
                var bc3Desc = new Texture2DDesc
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = useBc7 ? Format.FormatBC7Unorm : Format.FormatBC3Unorm,
                    SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                    Usage = Usage.Default,
                    BindFlags = (uint)BindFlag.ShaderResource,
                    CPUAccessFlags = 0,
                    MiscFlags = 0
                };

                ID3D11Texture2D* compressed = null;
                ID3D11ShaderResourceView* compressedSrv = null;
                var hr = _device->CreateTexture2D(&bc3Desc, null, &compressed);
                if (hr >= 0)
                {
                    fixed (byte* p = blocks)
                    {
                        var box = new Box { Left = 0, Top = 0, Front = 0, Right = (uint)w, Bottom = (uint)h, Back = 1 };
                        _context->UpdateSubresource((ID3D11Resource*)compressed, 0, &box, p, blockPitch, 0);
                    }
                    hr = _device->CreateShaderResourceView((ID3D11Resource*)compressed, null, &compressedSrv);
                }
                Release(compressed);
                if (hr < 0) throw new InvalidOperationException($"CreateTexture2D(BC3): 0x{hr:X8}");
                _log($"texture {(useBc7 ? "BC7" : "BC3")}: {Path.GetFileName(path)} {w}x{h}");
                return compressedSrv;
            }

            var texDesc = new Texture2DDesc
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.FormatB8G8R8A8Unorm,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = Usage.Default,
                BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.RenderTarget),
                CPUAccessFlags = 0,
                MiscFlags = (uint)ResourceMiscFlag.GenerateMips
            };

            ID3D11Texture2D* tex = null;
            ID3D11ShaderResourceView* srv = null;
            unsafe
            {
                var hr = _device->CreateTexture2D(&texDesc, null, &tex);
                if (hr < 0) throw new InvalidOperationException($"CreateTexture2D: 0x{hr:X8}");

                // Upload rows individually (D3D11 expects the source pitch).
                var rowData = new byte[data.Stride * h];
                for (var y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, rowData, y * data.Stride, data.Stride);

                fixed (byte* p = rowData)
                {
                    var box = new Box { Left = 0, Top = 0, Front = 0, Right = (uint)w, Bottom = (uint)h, Back = 1 };
                    _context->UpdateSubresource((ID3D11Resource*)tex, 0, &box, p, (uint)data.Stride, 0);
                }

                hr = _device->CreateShaderResourceView((ID3D11Resource*)tex, null, &srv);
                if (hr < 0) throw new InvalidOperationException($"CreateShaderResourceView: 0x{hr:X8}");
                _context->GenerateMips(srv);
                Release(tex);
            }
            return srv;
        }
        finally
        {
            resized.UnlockBits(data);
        }
    }

    private unsafe void UpdateSceneConstants(double time, double mouseX, double mouseY, AudioFrame audio)
    {
        var c = new SceneConstants
        {
            P0 = new Vector4((float)time, (float)mouseX, (float)mouseY, _renderHeight / (float)Math.Max(1, _renderWidth)),
            P1 = new Vector4(_renderWidth, _renderHeight, (float)audio.Level, (float)audio.Bass),
            P2 = new Vector4((float)audio.Mid, (float)audio.Treble, ShaderIntensity, 0),
            P3 = new Vector4(
                (float)audio.Bins.ElementAtOrDefault(0),
                (float)audio.Bins.ElementAtOrDefault(1),
                (float)audio.Bins.ElementAtOrDefault(2),
                (float)audio.Bins.ElementAtOrDefault(3)),
            P4 = new Vector4(
                (float)audio.Bins.ElementAtOrDefault(4),
                (float)audio.Bins.ElementAtOrDefault(5),
                (float)audio.Bins.ElementAtOrDefault(6),
                (float)audio.Bins.ElementAtOrDefault(7))
        };
        UpdateBuffer(_sceneCb, &c, SceneCbSize);
    }

    private unsafe void UpdateLayerConstants(RenderLayer layer)
    {
        var c = new LayerConstants
        {
            L0 = new Vector4(_renderWidth, _renderHeight, layer.PositionX * _renderWidth, layer.PositionY * _renderHeight),
            L1 = new Vector4(layer.SizeX * _renderWidth, layer.SizeY * _renderHeight, layer.ScaleX, layer.ScaleY),
            L2 = new Vector4(layer.Rotation, layer.Opacity, 1, 0),
            L3 = layer.Tint
        };
        UpdateBuffer(_layerCb, &c, LayerCbSize);
    }

    private unsafe void UpdateBuffer<T>(ID3D11Buffer* buffer, T* data, int size) where T : unmanaged
    {
        MappedSubresource mapped;
        // Dynamic constant buffers must use WRITE_DISCARD; plain WRITE returns
        // E_INVALIDARG (0x80070057) for this resource type.
        var hr = _context->Map((ID3D11Resource*)buffer, 0, Map.WriteDiscard, 0, &mapped);
        if (hr < 0)
            throw new InvalidOperationException($"Map constant buffer: 0x{hr:X8}");
        Buffer.MemoryCopy(data, (void*)mapped.PData, size, size);
        _context->Unmap((ID3D11Resource*)buffer, 0);
    }

    private static unsafe void Release<T>(T* p) where T : unmanaged
    {
        if (p is not null)
            ((IUnknown*)p)->Release();
    }

    private static unsafe void GetClientSize(IntPtr hwnd, out int width, out int height)
    {
        if (!GetClientRect(hwnd, out var rc))
        {
            width = 1920;
            height = 1080;
            return;
        }
        width = Math.Max(1, rc.Right - rc.Left);
        height = Math.Max(1, rc.Bottom - rc.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Vector4 P0;
        public Vector4 P1;
        public Vector4 P2;
        public Vector4 P3;
        public Vector4 P4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LayerConstants
    {
        public Vector4 L0;
        public Vector4 L1;
        public Vector4 L2;
        public Vector4 L3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleView
    {
        public Vector4 P0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlitParams
    {
        public Vector4 P0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    public unsafe void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var layer in _layers)
        {
            if (layer.PixelShader is not null && layer.PixelShader != _colorPs && layer.PixelShader != _imagePs)
                Release(layer.PixelShader);
        }
        _layers.Clear();

        foreach (var srv in _textureCache.Values)
            Release((ID3D11ShaderResourceView*)srv);
        _textureCache.Clear();

        Release(_particleVb);
        Release(_particleViewCb);
        Release(_particleLayout);
        Release(_particlePs);
        Release(_particleVs);
        Release(_adapter3);
        Release(_sceneSrv);
        Release(_sceneRtv);
        Release(_sceneTexture);
        Release(_backRtv);
        Release(_gpuEnd);
        Release(_gpuStart);
        Release(_gpuDisjoint);
        Release(_blitPs);
        Release(_blitCb);
        Release(_raster);
        Release(_blend);
        Release(_sampler);
        Release(_layerCb);
        Release(_sceneCb);
        Release(_imagePs);
        Release(_colorPs);
        Release(_vs);
        Release(_swapChain);
        Release(_context);
        Release(_device);
    }

    private sealed class RenderLayer
    {
        public string Name = "";
        public string Type = "color";
        public float PositionX, PositionY;
        public float SizeX = 1, SizeY = 1;
        public float ScaleX = 1, ScaleY = 1;
        public float Rotation;
        public float Opacity = 1;
        public bool Visible = true;
        public Vector4 Tint = Vector4.One;
        public unsafe ID3D11PixelShader* PixelShader;
        public unsafe ID3D11ShaderResourceView* Srv;
        public ParticleLayer? Particles;
        public readonly List<IntPtr> Textures = new();
        public float ParticleSpeed = 1.0f;
        public int? RequestedParticleCount;
    }

    private const string VertexShader = """
        float4 main(uint id : SV_VertexID) : SV_Position
        {
            float2 p = float2((id << 1) & 2, id & 2);
            return float4(p * 2.0 - 1.0, 0, 1);
        }
        """;

    private const string ColorShader = """
        cbuffer LayerParams : register(b1)
        {
            float4 L0; float4 L1; float4 L2; float4 L3;
        };
        float2 ComputeUV(float2 pixel)
        {
            float2 d = pixel - L0.zw;
            float s = sin(-L2.x);
            float c = cos(-L2.x);
            d = float2(c * d.x - s * d.y, s * d.x + c * d.y);
            d /= max(abs(L1.zw), float2(0.0001, 0.0001));
            return d / max(L1.xy, float2(1.0, 1.0)) + 0.5;
        }
        float4 main(float4 pos : SV_Position) : SV_Target
        {
            float2 uv = ComputeUV(pos.xy);
            if (any(uv < 0.0) || any(uv > 1.0))
                discard;
            return L3 * L2.y;
        }
        """;

    private const string BlitShader = """
        cbuffer BlitParams : register(b3)
        {
            float4 Screen; // width, height, 0, 0
        };

        Texture2D sceneTex : register(t0);
        SamplerState sceneSampler : register(s0);

        float4 main(float4 pos : SV_Position) : SV_Target
        {
            float2 uv = pos.xy / Screen.xy;
            return sceneTex.Sample(sceneSampler, uv);
        }
        """;

    private const string ParticleVertexShader = """
        cbuffer ParticleView : register(b2)
        {
            float4 P0; // width, height, 0, 0
        };

        struct VSIn
        {
            float2 pos : POSITION;
            float2 local : TEXCOORD0;
            float4 color : COLOR0;
        };

        struct VSOut
        {
            float4 pos : SV_Position;
            float2 local : TEXCOORD0;
            float4 color : COLOR0;
        };

        VSOut main(VSIn i)
        {
            VSOut o;
            o.pos = float4(
                (i.pos.x / P0.x) * 2.0 - 1.0,
                1.0 - (i.pos.y / P0.y) * 2.0,
                0, 1);
            o.local = i.local;
            o.color = i.color;
            return o;
        }
        """;

    private const string ParticlePixelShader = """
        struct PSIn
        {
            float4 pos : SV_Position;
            float2 local : TEXCOORD0;
            float4 color : COLOR0;
        };

        float4 main(PSIn i) : SV_Target
        {
            float d = dot(i.local, i.local);
            if (d > 1.0)
                discard;
            float soft = saturate(1.0 - d);
            return float4(i.color.rgb, i.color.a * soft);
        }
        """;

    private const string ImageShader = """
        cbuffer LayerParams : register(b1)
        {
            float4 L0; float4 L1; float4 L2; float4 L3;
        };
        Texture2D tex : register(t0);
        SamplerState samp : register(s0);
        float2 ComputeUV(float2 pixel)
        {
            float2 d = pixel - L0.zw;
            float s = sin(-L2.x);
            float c = cos(-L2.x);
            d = float2(c * d.x - s * d.y, s * d.x + c * d.y);
            d /= max(abs(L1.zw), float2(0.0001, 0.0001));
            return d / max(L1.xy, float2(1.0, 1.0)) + 0.5;
        }
        float4 main(float4 pos : SV_Position) : SV_Target
        {
            float2 uv = ComputeUV(pos.xy);
            if (any(uv < 0.0) || any(uv > 1.0))
                discard;
            return tex.Sample(samp, uv) * L3 * L2.y;
        }
        """;
}

internal static class ShaderCompiler
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate UIntPtr GetBufferSizeDelegate(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr unknown);

    public static byte[] Compile(string source, string entry, string target)
    {
        var cachePath = GetCachePath(source, entry, target);
        if (cachePath is not null)
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    var cached = File.ReadAllBytes(cachePath);
                    if (cached.Length > 0)
                        return cached;
                }
            }
            catch
            {
                // Cache is an optimization only; compile on any cache problem.
            }
        }

        var sourcePtr = Marshal.StringToHGlobalAnsi(source);
        IntPtr code = IntPtr.Zero;
        IntPtr errors = IntPtr.Zero;
        try
        {
            // 0x1000 = D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY. The translated
            // shader keeps GLSL-style globals (fragColor/g_uv) which the legacy
            // compatibility mode permits mutating.
            var hr = D3DCompile(sourcePtr, (nuint)source.Length, null, IntPtr.Zero, IntPtr.Zero,
                entry, target, 0x1000, 0, out code, out errors);
            if (hr < 0)
            {
                var message = BlobToString(errors) ?? $"D3DCompile failed: 0x{hr:X8}";
                throw new InvalidOperationException(message);
            }

            var size = (int)GetBlobSize(code);
            var data = new byte[size];
            Marshal.Copy(GetBlobPointer(code), data, 0, size);
            if (cachePath is not null)
            {
                try { File.WriteAllBytes(cachePath, data); }
                catch { /* cache only */ }
            }
            return data;
        }
        finally
        {
            if (code != IntPtr.Zero) ReleaseBlob(code);
            if (errors != IntPtr.Zero) ReleaseBlob(errors);
            Marshal.FreeHGlobal(sourcePtr);
        }
    }

    private static string? GetCachePath(string source, string entry, string target)
    {
        try
        {
            var key = "scene-dxbc-v1|" + entry + "|" + target + "|" + source;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var dir = Path.Combine(Path.GetTempPath(), "Wallerpaper", "SceneShaders");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, hash + ".dxbc");
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr GetBlobPointer(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fn = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
        return fn(blob);
    }

    private static UIntPtr GetBlobSize(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fn = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size));
        return fn(blob);
    }

    private static void ReleaseBlob(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fn = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
        fn(blob);
    }

    private static string? BlobToString(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return null;
        var size = (int)GetBlobSize(blob);
        if (size <= 0)
            return null;
        var data = new byte[size];
        Marshal.Copy(GetBlobPointer(blob), data, 0, size);
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        IntPtr pSrcData, nuint SrcDataSize, string? pSourceName,
        IntPtr pDefines, IntPtr pInclude, string pEntrypoint, string pTarget,
        uint Flags1, uint Flags2, out IntPtr ppCode, out IntPtr ppErrorMsgs);
}
