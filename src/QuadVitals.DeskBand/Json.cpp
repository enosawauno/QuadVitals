#include "Json.h"

#include <stdlib.h>

namespace pb
{
    namespace
    {
        struct Parser
        {
            const std::string& s;
            size_t i = 0;
            bool ok = true;

            explicit Parser(const std::string& text) : s(text) {}

            void SkipWhitespace()
            {
                while (i < s.size())
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                    break;
                }
            }

            bool Consume(char expected)
            {
                SkipWhitespace();
                if (i < s.size() && s[i] == expected) { i++; return true; }
                ok = false;
                return false;
            }

            bool ParseValue(JsonValue& out, int depth)
            {
                if (depth > 32) { ok = false; return false; } // pathological nesting guard
                SkipWhitespace();
                if (i >= s.size()) { ok = false; return false; }

                char c = s[i];
                if (c == '{') return ParseObject(out, depth);
                if (c == '[') return ParseArray(out, depth);
                if (c == '"') { out.kind = JsonValue::String; return ParseString(out.stringValue); }
                if (c == 't' || c == 'f') return ParseBool(out);
                if (c == 'n') return ParseNull(out);
                return ParseNumber(out);
            }

            bool ParseObject(JsonValue& out, int depth)
            {
                out.kind = JsonValue::Object;
                if (!Consume('{')) return false;
                SkipWhitespace();
                if (i < s.size() && s[i] == '}') { i++; return true; }

                while (ok)
                {
                    std::wstring key;
                    SkipWhitespace();
                    if (!ParseString(key)) return false;
                    if (!Consume(':')) return false;

                    JsonValue value;
                    if (!ParseValue(value, depth + 1)) return false;
                    out.objectValue.emplace_back(key, value);

                    SkipWhitespace();
                    if (i < s.size() && s[i] == ',') { i++; continue; }
                    if (i < s.size() && s[i] == '}') { i++; return true; }
                    ok = false;
                }
                return false;
            }

            bool ParseArray(JsonValue& out, int depth)
            {
                out.kind = JsonValue::Array;
                if (!Consume('[')) return false;
                SkipWhitespace();
                if (i < s.size() && s[i] == ']') { i++; return true; }

                while (ok)
                {
                    JsonValue value;
                    if (!ParseValue(value, depth + 1)) return false;
                    out.arrayValue.push_back(value);

                    SkipWhitespace();
                    if (i < s.size() && s[i] == ',') { i++; continue; }
                    if (i < s.size() && s[i] == ']') { i++; return true; }
                    ok = false;
                }
                return false;
            }

            bool ParseString(std::wstring& out)
            {
                SkipWhitespace();
                if (i >= s.size() || s[i] != '"') { ok = false; return false; }
                i++;

                std::string utf8;
                while (i < s.size())
                {
                    char c = s[i++];
                    if (c == '"') { out = Utf8ToWide(utf8); return true; }
                    if (c != '\\') { utf8.push_back(c); continue; }
                    if (i >= s.size()) break;

                    char esc = s[i++];
                    switch (esc)
                    {
                    case '"': utf8.push_back('"'); break;
                    case '\\': utf8.push_back('\\'); break;
                    case '/': utf8.push_back('/'); break;
                    case 'b': utf8.push_back('\b'); break;
                    case 'f': utf8.push_back('\f'); break;
                    case 'n': utf8.push_back('\n'); break;
                    case 'r': utf8.push_back('\r'); break;
                    case 't': utf8.push_back('\t'); break;
                    case 'u':
                    {
                        // Keep the code unit as UTF-16 and rebuild the UTF-8 bytes for it later.
                        if (i + 4 > s.size()) { ok = false; return false; }
                        unsigned int code = (unsigned int)wcstoul(std::wstring(s.begin() + i, s.begin() + i + 4).c_str(), nullptr, 16);
                        i += 4;
                        wchar_t wide = (wchar_t)code;
                        std::string asUtf8 = WideToUtf8(std::wstring(1, wide));
                        utf8 += asUtf8;
                        break;
                    }
                    default: ok = false; return false;
                    }
                }
                ok = false;
                return false;
            }

            bool ParseBool(JsonValue& out)
            {
                out.kind = JsonValue::Bool;
                if (s.compare(i, 4, "true") == 0) { i += 4; out.boolValue = true; return true; }
                if (s.compare(i, 5, "false") == 0) { i += 5; out.boolValue = false; return true; }
                ok = false;
                return false;
            }

            bool ParseNull(JsonValue& out)
            {
                out.kind = JsonValue::Null;
                if (s.compare(i, 4, "null") == 0) { i += 4; return true; }
                ok = false;
                return false;
            }

            bool ParseNumber(JsonValue& out)
            {
                out.kind = JsonValue::Number;
                size_t start = i;
                if (i < s.size() && (s[i] == '-' || s[i] == '+')) i++;
                while (i < s.size())
                {
                    char c = s[i];
                    bool numeric = (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+';
                    if (!numeric) break;
                    i++;
                }
                if (i == start) { ok = false; return false; }
                out.numberValue = atof(s.substr(start, i - start).c_str());
                return true;
            }
        };
    }

    const JsonValue* JsonValue::Get(const wchar_t* key) const
    {
        if (kind != Object || key == nullptr) return nullptr;
        for (const auto& pair : objectValue)
        {
            if (pair.first == key) return &pair.second;
        }
        return nullptr;
    }

    std::wstring JsonValue::GetString(const wchar_t* key, const wchar_t* fallback) const
    {
        const JsonValue* value = Get(key);
        if (value == nullptr || !value->IsString()) return fallback ? fallback : L"";
        return value->stringValue;
    }

    int JsonValue::GetInt(const wchar_t* key, int fallback) const
    {
        const JsonValue* value = Get(key);
        if (value == nullptr || !value->IsNumber()) return fallback;
        return (int)(value->numberValue + (value->numberValue >= 0 ? 0.5 : -0.5));
    }

    bool JsonValue::GetBool(const wchar_t* key, bool fallback) const
    {
        const JsonValue* value = Get(key);
        if (value == nullptr || !value->IsBool()) return fallback;
        return value->boolValue;
    }

    bool ParseJson(const std::string& utf8, JsonValue& out)
    {
        Parser parser(utf8);
        JsonValue value;
        if (!parser.ParseValue(value, 0)) return false;
        parser.SkipWhitespace();
        if (parser.i != utf8.size()) return false; // trailing garbage means a torn file
        out = value;
        return true;
    }

    std::wstring Utf8ToWide(const std::string& text)
    {
        if (text.empty()) return std::wstring();
        int needed = MultiByteToWideChar(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0);
        if (needed <= 0) return std::wstring();
        std::wstring wide((size_t)needed, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), (int)text.size(), &wide[0], needed);
        return wide;
    }

    std::string WideToUtf8(const std::wstring& text)
    {
        if (text.empty()) return std::string();
        int needed = WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0, nullptr, nullptr);
        if (needed <= 0) return std::string();
        std::string narrow((size_t)needed, '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), &narrow[0], needed, nullptr, nullptr);
        return narrow;
    }
}
