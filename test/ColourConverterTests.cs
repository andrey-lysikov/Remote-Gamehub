//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Media;
using RemoteGameHub.Native;
using Xunit;

namespace RemoteGameHub.Tests;

// The colour conversion, as far as it goes without a graphics card: the shader is built by the
// compiler Windows ships, and the matrix behind it is arithmetic.
public class ColourConverterTests
{
    [Theory]
    [InlineData(false, YuvColour.Rec709, false)]
    [InlineData(false, YuvColour.Rec709, true)]
    [InlineData(false, YuvColour.Rec601, false)]
    [InlineData(true, YuvColour.Rec2020, false)]
    [InlineData(true, YuvColour.Rec2020, true)]
    public void The_shader_compiles(bool hdr, int space, bool fullRange)
    {
        var source = ColourConverter.Hlsl(hdr, new YuvColour(space, fullRange));

        foreach (var (entry, target) in new[] { ("vs", "vs_5_0"), ("luma", "ps_5_0"),
                                                ("chroma", "ps_5_0") })
        {
            var bytecode = D3DCompiler.Compile(source, entry, target);
            Assert.NotEmpty(bytecode);
        }
    }

    // White is 940 of 1023 (235 of 255) in limited range and the top code in full, and neutral
    // chroma is the middle of the range either way. All as codes over the top code, as unorm takes.
    [Theory]
    [InlineData(10, false, 940.0, 512.0)]
    [InlineData(10, true, 1023.0, 512.0)]
    [InlineData(8, false, 235.0, 128.0)]
    [InlineData(8, true, 255.0, 128.0)]
    public void White_and_neutral_land_where_the_standard_says(int bits, bool fullRange,
                                                               double white, double neutral)
    {
        var codes = (1 << bits) - 1.0;
        var (y, u, v) = ColourConverter.Vectors(bits, new YuvColour(YuvColour.Rec709, fullRange));

        Assert.Equal(white / codes, y.R + y.G + y.B + y.Add, 6);

        // Chroma of any grey is the offset alone: the three coefficients of each vector sum to
        // zero, which is what makes a colourless pixel colourless.
        Assert.Equal(0.0, u.R + u.G + u.B, 9);
        Assert.Equal(0.0, v.R + v.G + v.B, 9);
        Assert.Equal(neutral / codes, u.Add, 6);
        Assert.Equal(neutral / codes, v.Add, 6);
    }

    // Black is 16 (64 in ten bits) in limited range and zero in full: the lifted black that a
    // client reading one as the other shows as grey.
    [Theory]
    [InlineData(8, false, 16.0)]
    [InlineData(8, true, 0.0)]
    [InlineData(10, false, 64.0)]
    public void Black_lands_where_the_standard_says(int bits, bool fullRange, double black)
    {
        var (y, _, _) = ColourConverter.Vectors(bits, new YuvColour(YuvColour.Rec709, fullRange));

        Assert.Equal(black / ((1 << bits) - 1.0), y.Add, 6);
    }

    // The luma weights are the matrix's own: Rec. 709 gives green most of the say.
    [Fact]
    public void Each_matrix_weighs_luma_its_own_way()
    {
        var (rec709, _, _) = ColourConverter.Vectors(8, new YuvColour(YuvColour.Rec709, true));
        var (rec601, _, _) = ColourConverter.Vectors(8, new YuvColour(YuvColour.Rec601, true));

        Assert.Equal(0.2126, rec709.R, 6);
        Assert.Equal(0.0722, rec709.B, 6);
        Assert.Equal(0.299, rec601.R, 6);
        Assert.Equal(0.114, rec601.B, 6);
    }

    // Red is all of the V vector's positive half and green is all of U's, which is the sign
    // convention: a mistake here shows as blue and red swapped in every stream.
    [Fact]
    public void The_chroma_vectors_point_the_way_the_standard_says()
    {
        var (_, u, v) = ColourConverter.Vectors(10, new YuvColour(YuvColour.Rec2020, false));

        Assert.True(u.B > 0 && u.R < 0 && u.G < 0);
        Assert.True(v.R > 0 && v.G < 0 && v.B < 0);
    }
}
