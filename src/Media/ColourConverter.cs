//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// The YUV the shader writes and the stream says it is: Limelight.h's COLORSPACE_* (0 Rec. 601,
// 1 Rec. 709, 2 Rec. 2020) and the range. The client asks for both in encoderCscMode.
internal readonly record struct YuvColour(int Space, bool FullRange)
{
    internal const int Rec601 = 0;
    internal const int Rec709 = 1;
    internal const int Rec2020 = 2;

    // Kr and Kb from H.273 table 4; Kg follows from the two.
    internal (double Kr, double Kb) Coefficients => Space switch
    {
        Rec601 => (0.299, 0.114),
        Rec2020 => (0.2627, 0.0593),
        _ => (0.2126, 0.0722),
    };

    internal string Name => (Space switch { Rec601 => "BT.601", Rec2020 => "BT.2020", _ => "BT.709" }) +
                            (FullRange ? ", full range" : ", limited range");
}

// The captured desktop turned into the YUV the encoder takes, as the client asked for it: an HDR
// desktop's linear scRGB into ten-bit BT.2020 PQ, an ordinary one's sRGB into eight-bit YUV.
internal sealed unsafe class ColourConverter : IDisposable
{
    // scRGB says 1.0 is eighty nits, and PQ is written against absolute luminance.
    private const double ScRgbWhiteNits = 80.0;

    private readonly void* _device;
    private readonly void* _context;

    private void* _vertexShader;
    private void* _lumaShader;
    private void* _chromaShader;
    private void* _sampler;
    private void* _sourceView;
    private void* _lumaTarget;
    private void* _chromaTarget;
    private void* _output;
    private nint _sourceTexture;

    // The pointer overlay handed to Convert, and its view: cached the same way as the source, by
    // the texture handle, because DesktopDuplicator hands back the same one every frame.
    private void* _overlayView;
    private nint _overlayTexture;

    // A single transparent pixel, bound instead of a real overlay when there is nothing to draw:
    // the shader always has something at t1, and never needs to know whether it is real.
    private void* _dummyOverlay;
    private void* _dummyOverlayView;

    private bool _disposed;

    private readonly bool _hdr;
    private readonly YuvColour _colour;

    internal int Width { get; }
    internal int Height { get; }

    // The P010 (HDR) or NV12 texture the encoder is handed. Its content is what Convert last wrote.
    internal nint Output => (nint)_output;

    private ColourConverter(void* device, void* context, int width, int height, bool hdr,
                            YuvColour colour)
    {
        _device = device;
        _context = context;
        Width = width;
        Height = height;
        _hdr = hdr;
        _colour = colour;
    }

    // Builds the shader for one stream. Width and height must be even: a chroma sample covers two
    // pixels each way, and the duplication never hands back an odd desktop.
    internal static ColourConverter Open(nint device, nint context, int width, int height,
                                         bool hdr, YuvColour colour)
    {
        var converter = new ColourConverter((void*)device, (void*)context, width, height, hdr, colour);

        try
        {
            converter.Build();
            Log.Info(hdr
                ? $"the HDR desktop is converted to ten-bit BT.2020 PQ, {colour.Name}, before it is encoded"
                : $"the desktop is converted to eight-bit {colour.Name} before it is encoded");
            return converter;
        }
        catch
        {
            converter.Dispose();
            throw;
        }
    }

    // The frame the shader reads. Held until the texture changes, which is once per duplication:
    // a view is a driver object, and one per frame at 240 a second is not free.
    internal void Source(nint texture)
    {
        if (texture == _sourceTexture && _sourceView is not null) return;

        Com.ReleaseAndClear(ref _sourceView);
        _sourceView = D3D11.CreateShaderResourceView(_device, (void*)texture);
        _sourceTexture = texture;
    }

    // Draws the two planes, the pointer blended in from cursorOverlay if there is one. Nothing
    // here waits for the card: the encode that reads Output next is ordered behind it already.
    internal void Convert(nint texture, nint cursorOverlay = 0)
    {
        Source(texture);

        void* overlay;
        if (cursorOverlay != 0)
        {
            if (cursorOverlay != _overlayTexture || _overlayView is null)
            {
                Com.ReleaseAndClear(ref _overlayView);
                _overlayView = D3D11.CreateShaderResourceView(_device, (void*)cursorOverlay);
                _overlayTexture = cursorOverlay;
            }

            overlay = _overlayView;
        }
        else
        {
            overlay = _dummyOverlayView;
        }

        D3D11.IASetPrimitiveTopology(_context, D3D11.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11.VSSetShader(_context, _vertexShader);
        D3D11.PSSetShaderResources(_context, 0, _sourceView);
        D3D11.PSSetShaderResources(_context, 1, overlay);
        D3D11.PSSetSamplers(_context, _sampler);

        Plane(_lumaShader, _lumaTarget, Width, Height);
        Plane(_chromaShader, _chromaTarget, Width / 2, Height / 2);

        // The views are left bound otherwise, and the next frame's write into those same textures
        // would find them still in use as a shader input.
        D3D11.OMSetRenderTargets(_context, null);
    }

    private void Plane(void* shader, void* target, int width, int height)
    {
        var viewport = new D3D11Viewport
        {
            Width = width,
            Height = height,
            MaxDepth = 1,
        };

        D3D11.OMSetRenderTargets(_context, target);
        D3D11.RSSetViewports(_context, viewport);
        D3D11.PSSetShader(_context, shader);

        // Three vertices, no buffer: the shader makes a triangle that covers the target from the
        // vertex number alone, which is cheaper than a quad and needs no input layout.
        D3D11.Draw(_context, 3);
    }

    private void Build()
    {
        var source = Hlsl(_hdr, _colour);

        var vertex = D3DCompiler.Compile(source, "vs", "vs_5_0");
        var luma = D3DCompiler.Compile(source, "luma", "ps_5_0");
        var chroma = D3DCompiler.Compile(source, "chroma", "ps_5_0");

        fixed (byte* bytes = vertex) _vertexShader = D3D11.CreateVertexShader(_device, bytes, (nuint)vertex.Length);
        fixed (byte* bytes = luma) _lumaShader = D3D11.CreatePixelShader(_device, bytes, (nuint)luma.Length);
        fixed (byte* bytes = chroma) _chromaShader = D3D11.CreatePixelShader(_device, bytes, (nuint)chroma.Length);

        _sampler = D3D11.CreateSamplerState(_device, new D3D11SamplerDesc
        {
            // Linear, so that one chroma sample is the average of the four pixels it covers: at
            // half the size, the centre of a target pixel falls exactly between four of the source.
            Filter = D3D11.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11.D3D11_COMPARISON_NEVER,
            MaxLod = float.MaxValue,
        });

        _output = D3D11.CreateTexture2D(_device, new D3D11Texture2DDesc
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _hdr ? Dxgi.DXGI_FORMAT_P010 : Dxgi.DXGI_FORMAT_NV12,
            SampleCount = 1,
            Usage = D3D11.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11.D3D11_BIND_RENDER_TARGET | D3D11.D3D11_BIND_SHADER_RESOURCE,
        });

        _lumaTarget = D3D11.CreateRenderTargetView(_device, _output, new D3D11RenderTargetViewDesc
        {
            Format = _hdr ? Dxgi.DXGI_FORMAT_R16_UNORM : Dxgi.DXGI_FORMAT_R8_UNORM,
            ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
        });

        _chromaTarget = D3D11.CreateRenderTargetView(_device, _output, new D3D11RenderTargetViewDesc
        {
            Format = _hdr ? Dxgi.DXGI_FORMAT_R16G16_UNORM : Dxgi.DXGI_FORMAT_R8G8_UNORM,
            ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
        });

        BuildDummyOverlay();
    }

    // One pixel, transparent, bound at t1 when there is no pointer to draw. Zeroed through a
    // render target view rather than trusted to come up that way on its own.
    private void BuildDummyOverlay()
    {
        _dummyOverlay = D3D11.CreateTexture2D(_device, new D3D11Texture2DDesc
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = D3D11.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11.D3D11_BIND_SHADER_RESOURCE | D3D11.D3D11_BIND_RENDER_TARGET,
        });

        var view = D3D11.CreateRenderTargetView(_device, _dummyOverlay, new D3D11RenderTargetViewDesc
        {
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
        });

        try
        {
            var clear = stackalloc float[4];
            D3D11.ClearRenderTargetView(_context, view, clear);
        }
        finally
        {
            Com.Release(view);
        }

        _dummyOverlayView = D3D11.CreateShaderResourceView(_device, _dummyOverlay);
    }

    // The whole shader, written with this stream's numbers in it rather than fed a constant
    // buffer: it is built once per stream, and the arithmetic is then plain to read in the source.
    internal static string Hlsl(bool hdr, YuvColour colour)
    {
        var bits = hdr ? 10 : 8;
        var codes = (1 << bits) - 1.0;
        var (y, u, v) = Vectors(bits, colour);

        // P010 keeps the top ten bits of each word, which drops the fraction of a code rather than
        // rounding it; half a code added first makes it round. An eight-bit target rounds itself.
        var bias = hdr ? 0.5 / codes : 0.0;

        return $$"""
            Texture2D<float4> source : register(t0);

            // The pointer, drawn by GDI onto its own eight-bit BGRA texture the same size as
            // source (alpha zero where there is nothing); a one-pixel stand-in when there is none.
            Texture2D<float4> overlay : register(t1);

            SamplerState blend : register(s0);

            struct vertex { float4 position : SV_Position; float2 texel : TEXCOORD0; };

            // One triangle larger than the target, from the vertex number: no buffer, no layout.
            vertex vs(uint id : SV_VertexID)
            {
                vertex pixel;
                pixel.texel = float2((id << 1) & 2, id & 2);
                pixel.position = float4(pixel.texel * float2(2, -2) + float2(-1, 1), 0, 1);
                return pixel;
            }

            // SMPTE ST 2084, on absolute luminance in nits.
            float3 pq(float3 nits)
            {
                static const float m1 = 2610.0 / 4096.0 / 4;
                static const float m2 = 2523.0 / 4096.0 * 128;
                static const float c1 = 3424.0 / 4096.0;
                static const float c2 = 2413.0 / 4096.0 * 32;
                static const float c3 = 2392.0 / 4096.0 * 32;

                float3 l = pow(saturate(nits / 10000.0), m1);
                return pow((c1 + c2 * l) / (1 + c3 * l), m2);
            }

            // GDI drew the pointer in plain sRGB, with no notion of nits. Undoing the sRGB curve
            // puts it on the same Rec. 709 linear scale scRGB already uses for the desktop.
            float3 fromSrgb(float3 c)
            {
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            #if {{(hdr ? 1 : 0)}}
            // scRGB is linear on Rec. 709 primaries; Rec. 2100 wants Rec. 2020 primaries and PQ.
            float3 encode(float3 rgb)
            {
                static const float3x3 toRec2020 =
                {
                    0.627402, 0.329292, 0.043306,
                    0.069095, 0.919544, 0.011360,
                    0.016394, 0.088028, 0.895578
                };

                return pq(mul(toRec2020, rgb) * {{Number(ScRgbWhiteNits)}});
            }

            float3 picture(float4 desktop, float4 pointer)
            {
                return lerp(desktop.rgb, fromSrgb(pointer.rgb), pointer.a);
            }
            #else
            // An ordinary desktop is sRGB already, gamma and all, which is what the matrix takes.
            float3 encode(float3 rgb)
            {
                return saturate(rgb);
            }

            float3 picture(float4 desktop, float4 pointer)
            {
                return lerp(desktop.rgb, pointer.rgb, pointer.a);
            }
            #endif

            // A 4x4 ordered dither, fixed in place so it costs the encoder nothing frame to frame:
            // up to half a code either way, which the eye averages into the steps a gradient lacks.
            static const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

            float dither(float2 position)
            {
                uint2 p = uint2(position) & 3;
                return ((bayer[p.y * 4 + p.x] + 0.5) / 16.0 - 0.5) * {{Number(1 / codes)}} + {{Number(bias)}};
            }

            float luma(vertex pixel) : SV_Target
            {
                float4 desktop = source.Load(int3(pixel.position.xy, 0));
                float4 pointer = overlay.Load(int3(pixel.position.xy, 0));
                float3 c = encode(picture(desktop, pointer));
                return dot(float3({{Number(y.R)}}, {{Number(y.G)}}, {{Number(y.B)}}), c) + {{Number(y.Add)}}
                       + dither(pixel.position.xy);
            }

            float2 chroma(vertex pixel) : SV_Target
            {
                float4 desktop = source.Sample(blend, pixel.texel);
                float4 pointer = overlay.Sample(blend, pixel.texel);
                float3 c = encode(picture(desktop, pointer));
                return float2(
                    dot(float3({{Number(u.R)}}, {{Number(u.G)}}, {{Number(u.B)}}), c) + {{Number(u.Add)}}
                        + dither(pixel.position.xy),
                    dot(float3({{Number(v.R)}}, {{Number(v.G)}}, {{Number(v.B)}}), c) + {{Number(v.Add)}}
                        + dither(pixel.position.yx + 2));
            }
            """;
    }

    internal readonly record struct Vector(double R, double G, double B, double Add);

    // The matrix of H.273 section 8.3 for this many bits, written into a unorm target: the codes
    // are scaled by 2^bits - 1, which for ten bits puts them in the top ten bits of P010.
    internal static (Vector Y, Vector U, Vector V) Vectors(int bits, YuvColour colour)
    {
        var (kr, kb) = colour.Coefficients;
        var kg = 1.0 - kr - kb;
        var codes = (1 << bits) - 1.0;
        var step = (double)(1 << (bits - 8));

        var lumaScale = (colour.FullRange ? codes : 219 * step) / codes;
        var lumaOffset = (colour.FullRange ? 0 : 16 * step) / codes;
        var chromaScale = (colour.FullRange ? codes : 224 * step) / codes;
        var chromaOffset = (colour.FullRange ? 1 << (bits - 1) : 128 * step) / codes;

        return (
            new Vector(kr * lumaScale, kg * lumaScale, kb * lumaScale, lumaOffset),
            new Vector(-0.5 * kr / (1.0 - kb) * chromaScale, -0.5 * kg / (1.0 - kb) * chromaScale,
                       0.5 * chromaScale, chromaOffset),
            new Vector(0.5 * chromaScale, -0.5 * kg / (1.0 - kr) * chromaScale,
                       -0.5 * kb / (1.0 - kr) * chromaScale, chromaOffset));
    }

    private static string Number(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Com.ReleaseAndClear(ref _lumaTarget);
        Com.ReleaseAndClear(ref _chromaTarget);
        Com.ReleaseAndClear(ref _output);
        Com.ReleaseAndClear(ref _sourceView);
        Com.ReleaseAndClear(ref _overlayView);
        Com.ReleaseAndClear(ref _dummyOverlayView);
        Com.ReleaseAndClear(ref _dummyOverlay);
        Com.ReleaseAndClear(ref _sampler);
        Com.ReleaseAndClear(ref _vertexShader);
        Com.ReleaseAndClear(ref _lumaShader);
        Com.ReleaseAndClear(ref _chromaShader);
    }
}
