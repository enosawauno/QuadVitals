// Minimal JSON reader for settings.json. Deliberately tiny and total: it never throws and
// never calls back into the shell, because a malformed settings file must not take Explorer
// down with it.
#pragma once

#include <windows.h>
#include <string>
#include <vector>
#include <utility>

namespace pb
{
    struct JsonValue
    {
        enum Kind { Null, Bool, Number, String, Array, Object };

        Kind kind = Null;
        bool boolValue = false;
        double numberValue = 0.0;
        std::wstring stringValue;
        std::vector<JsonValue> arrayValue;
        std::vector<std::pair<std::wstring, JsonValue>> objectValue;

        bool IsObject() const { return kind == Object; }
        bool IsArray() const { return kind == Array; }
        bool IsString() const { return kind == String; }
        bool IsNumber() const { return kind == Number; }
        bool IsBool() const { return kind == Bool; }

        // Object member lookup; returns nullptr when missing or when this is not an object.
        const JsonValue* Get(const wchar_t* key) const;
        std::wstring GetString(const wchar_t* key, const wchar_t* fallback) const;
        int GetInt(const wchar_t* key, int fallback) const;
        bool GetBool(const wchar_t* key, bool fallback) const;
    };

    // Parses UTF-8 JSON text. Returns false on malformed input (out is then untouched).
    bool ParseJson(const std::string& utf8, JsonValue& out);

    // UTF-8 <-> UTF-16 helpers (no exceptions).
    std::wstring Utf8ToWide(const std::string& text);
    std::string WideToUtf8(const std::wstring& text);
}
