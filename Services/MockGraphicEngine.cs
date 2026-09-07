using System;
using System.IO;
using ClosedXML.Excel;
using ClosedXML.Graphics;

namespace CostFlow.Controllers
{
    // ClosedXML mock graphic engine for headless Linux/Windows environments without GDI fonts
    public class MockGraphicEngine : IXLGraphicEngine
    {
        public XLPictureInfo GetPictureInfo(Stream imageStream, ClosedXML.Excel.Drawings.XLPictureFormat expectedFormat)
        {
            throw new NotImplementedException();
        }

        public double GetTextHeight(IXLFontBase font, double dpiY)
        {
            return font.FontSize * 1.2;
        }

        public double GetTextWidth(string text, IXLFontBase font, double dpiX)
        {
            return text.Length * (font.FontSize * 0.6);
        }

        public double GetMaxDigitWidth(IXLFontBase font, double dpiX)
        {
            return font.FontSize * 0.6;
        }

        public double GetDescent(IXLFontBase font, double dpiY)
        {
            return 0.0;
        }

        public GlyphBox GetGlyphBox(ReadOnlySpan<int> graphemeCluster, IXLFontBase font, Dpi dpi)
        {
            return new GlyphBox(0f, 0f, 0f);
        }
    }
}
