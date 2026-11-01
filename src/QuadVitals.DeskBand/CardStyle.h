// Shared drawing pieces for the band's own popup surfaces (tooltip card, context menu).
//
// Both are layered popups drawn once into a 32bpp ARGB bitmap: rounded card, 1 px edge, soft
// shadow, premultiplied before UpdateLayeredWindow. Keeping the primitives here means the menu
// and the tooltip cannot drift apart in radius, colour or font.
#pragma once

#include <windows.h>
#include <objidl.h>
#include <gdiplus.h>
#include <string>

namespace pb
{
    namespace card
    {
        static const BYTE kSurfaceR = 0x2B;
        static const BYTE kSurfaceG = 0x2B;
        static const BYTE kSurfaceB = 0x2B;
        static const BYTE kEdgeR = 0x45;
        static const BYTE kEdgeG = 0x45;
        static const BYTE kEdgeB = 0x45;
        static const BYTE kHoverR = 0x3A;
        static const BYTE kHoverG = 0x3A;
        static const BYTE kHoverB = 0x3A;

        inline void AddRoundedRect(Gdiplus::GraphicsPath& path, int x, int y, int width, int height, int radius)
        {
            if (radius * 2 > width) radius = width / 2;
            if (radius * 2 > height) radius = height / 2;
            if (radius < 1) radius = 1;
            Gdiplus::REAL r = (Gdiplus::REAL)radius;
            Gdiplus::REAL right = (Gdiplus::REAL)(x + width - 1);
            Gdiplus::REAL bottom = (Gdiplus::REAL)(y + height - 1);
            Gdiplus::REAL left = (Gdiplus::REAL)x;
            Gdiplus::REAL top = (Gdiplus::REAL)y;

            path.AddArc(left, top, r * 2, r * 2, 180, 90);
            path.AddArc(right - r * 2, top, r * 2, r * 2, 270, 90);
            path.AddArc(right - r * 2, bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(left, bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
        }

        /// <summary>UpdateLayeredWindow wants premultiplied alpha; GDI+ writes straight ARGB.</summary>
        inline void Premultiply(void* bits, int count)
        {
            unsigned char* pixels = (unsigned char*)bits;
            for (int i = 0; i < count; i++)
            {
                unsigned char* pixel = pixels + (size_t)i * 4;
                unsigned int alpha = pixel[3];
                if (alpha == 255) continue;
                if (alpha == 0) { pixel[0] = pixel[1] = pixel[2] = 0; continue; }
                pixel[0] = (unsigned char)(pixel[0] * alpha / 255);
                pixel[1] = (unsigned char)(pixel[1] * alpha / 255);
                pixel[2] = (unsigned char)(pixel[2] * alpha / 255);
            }
        }

        inline HFONT MakeFont(int height)
        {
            LOGFONTW lf = { 0 };
            lf.lfHeight = -height;
            lf.lfWeight = FW_NORMAL;
            lf.lfQuality = ANTIALIASED_QUALITY;
            lf.lfCharSet = DEFAULT_CHARSET;
            wcscpy_s(lf.lfFaceName, L"Microsoft YaHei UI");
            return CreateFontIndirectW(&lf);
        }

        /// <summary>Card body + edge; the caller draws its own content on top.</summary>
        inline void PaintCard(Gdiplus::Graphics& graphics, int x, int y, int width, int height,
                              int radius, int shadow, double scale)
        {
            for (int ring = shadow; ring >= 1; ring -= 2)
            {
                Gdiplus::GraphicsPath halo;
                AddRoundedRect(halo, x - ring, y - ring, width + ring * 2, height + ring * 2, radius + ring);
                Gdiplus::SolidBrush shade(Gdiplus::Color((BYTE)(6 + (shadow - ring) * 3), 0, 0, 0));
                graphics.FillPath(&shade, &halo);
            }

            Gdiplus::GraphicsPath path;
            AddRoundedRect(path, x, y, width, height, radius);
            Gdiplus::SolidBrush card(Gdiplus::Color(255, kSurfaceR, kSurfaceG, kSurfaceB));
            graphics.FillPath(&card, &path);
            Gdiplus::Pen edge(Gdiplus::Color(255, kEdgeR, kEdgeG, kEdgeB), (Gdiplus::REAL)(1.0 * scale));
            graphics.DrawPath(&edge, &path);
        }

        inline int ShadowFor(double scale)
        {
            int shadow = (int)(9.0 * scale + 0.5);
            return shadow < 4 ? 4 : shadow;
        }
    }
}
