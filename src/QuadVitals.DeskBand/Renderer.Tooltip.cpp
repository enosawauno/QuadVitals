#include "Renderer.h"

#include <strsafe.h>

// The tooltip text lives in its own translation unit: Renderer.cpp is already at the size limit
// the repository allows for one source file, and this function shares nothing with the painting
// code beyond the public Renderer helpers.
namespace pb
{
    std::wstring Renderer::BuildTooltip(const BandSettings& settings, const Snapshot& snapshot, bool agentAlive)
    {
        std::wstring text;

        for (const Element& element : settings.items)
        {
            if (!element.enabled) continue;

            if (element.type == ElementType::Bar || element.type == ElementType::Pie)
            {
                text += MetricDisplayName(element.metric);
                text += L"  ";
                text += agentAlive ? FormatMetric(element.metric, snapshot, false) : L"--";
                text += L"\n";
            }
            else if (element.type == ElementType::Text)
            {
                for (const TextLine& line : element.lines)
                {
                    text += MetricDisplayName(line.metric);
                    text += L"  ";
                    text += agentAlive ? FormatMetric(line.metric, snapshot, false, element.drive) : L"--";
                    text += L"\n";
                }
            }
        }

        wchar_t buffer[256];
        if (snapshot.memoryTotalBytes > 0)
        {
            StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"\u5185\u5B58  %.1f / %.1f GB\n",
                             (double)snapshot.memoryUsedBytes / (1024.0 * 1024.0 * 1024.0),
                             (double)snapshot.memoryTotalBytes / (1024.0 * 1024.0 * 1024.0));
            text += buffer;
        }

        if (snapshot.adapterName[0] != L'\0')
        {
            text += L"\u7F51\u5361  ";
            text += snapshot.adapterName;
            text += L"\n";
        }

        if (!agentAlive)
        {
            text += snapshot.timestampUtcTicks == 0
                ? L"Agent \u672A\u8FD0\u884C\n"
                : L"Agent \u79BB\u7EBF\uFF08\u6570\u636E\u9648\u65E7\uFF09\n";
        }
        else
        {
            StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"\u66F4\u65B0\u4E8E %.1f \u79D2\u524D\n", snapshot.AgeSeconds());
            text += buffer;
        }

        text += L"\u53CC\u51FB\u6253\u5F00\u4EEA\u8868\u76D8\uFF0C\u53F3\u952E\u67E5\u770B\u83DC\u5355";
        return text;
    }
}
