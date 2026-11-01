#include "MetricsClient.h"

namespace pb
{
    namespace
    {
        const wchar_t* kMapName = L"Local\\QuadVitals.Metrics.v1";
        const int kSnapshotBytes = 364;   // v3: per-volume disk-I/O table after the v2 floats
        const unsigned int kMagic = 0x534C5550; // 'PULS' little endian

        unsigned int ReadUInt32(const unsigned char* b, int offset)
        {
            return (unsigned int)b[offset] | ((unsigned int)b[offset + 1] << 8) |
                   ((unsigned int)b[offset + 2] << 16) | ((unsigned int)b[offset + 3] << 24);
        }

        unsigned short ReadUInt16(const unsigned char* b, int offset)
        {
            return (unsigned short)(b[offset] | (b[offset + 1] << 8));
        }

        long long ReadInt64(const unsigned char* b, int offset)
        {
            long long value = 0;
            for (int i = 7; i >= 0; i--) value = (value << 8) | b[offset + i];
            return value;
        }

        float ReadSingle(const unsigned char* b, int offset)
        {
            float value = 0.0f;
            memcpy(&value, b + offset, sizeof(value));
            return value;
        }

        double ReadDouble(const unsigned char* b, int offset)
        {
            double value = 0.0;
            memcpy(&value, b + offset, sizeof(value));
            return value;
        }

        // FILETIME counts 100 ns ticks from 1601-01-01, DateTime.Ticks from 0001-01-01.
        // The agent stamps DateTime.UtcNow.Ticks, so both sides must share one epoch or the
        // "age" comes out negative and every snapshot looks offline.
        const long long kFileTimeToDateTimeTicks = 504911232000000000LL;

        long long NowUtcTicks()
        {
            // GetSystemTimePreciseAsFileTime needs Windows 8; the band keeps the 0x0601 baseline
            // and 15 ms resolution is far below the 1 s publish rate.
            FILETIME ft;
            GetSystemTimeAsFileTime(&ft);
            ULARGE_INTEGER value;
            value.LowPart = ft.dwLowDateTime;
            value.HighPart = ft.dwHighDateTime;
            return (long long)value.QuadPart + kFileTimeToDateTimeTicks;
        }
    }

    bool Snapshot::IsAlive() const
    {
        if ((flags & FlagAgentAlive) == 0) return false;
        if (timestampUtcTicks <= 0) return false;
        long long age = NowUtcTicks() - timestampUtcTicks;
        return age >= 0 && age <= 20LL * 1000 * 1000; // 2 s in 100 ns ticks
    }

    double Snapshot::AgeSeconds() const
    {
        if (timestampUtcTicks <= 0) return -1.0;
        return (double)(NowUtcTicks() - timestampUtcTicks) / 1.0e7;
    }

    bool Snapshot::TryVolume(wchar_t letter, bool& valid, double& read, double& write) const
    {
        valid = false;
        read = 0;
        write = 0;
        if (letter == 0) return false;
        wchar_t wanted = (letter >= L'a' && letter <= L'z') ? (wchar_t)(letter - L'a' + L'A') : letter;
        for (int i = 0; i < volumeCount && i < MaxVolumes; i++)
        {
            if (volumeLetters[i] != wanted) continue;
            valid = volumeValid[i];
            read = volumeRead[i];
            write = volumeWrite[i];
            return true;
        }
        return false;
    }

    MetricsClient::~MetricsClient()
    {
        CloseChannel();
    }

    bool MetricsClient::OpenChannel()
    {
        if (_view != nullptr) return true;

        _map = OpenFileMappingW(FILE_MAP_READ, FALSE, kMapName);
        if (_map == nullptr) return false; // agent not running yet

        _view = (const unsigned char*)MapViewOfFile(_map, FILE_MAP_READ, 0, 0, kSnapshotBytes);
        if (_view == nullptr)
        {
            CloseHandle(_map);
            _map = nullptr;
            return false;
        }
        return true;
    }

    void MetricsClient::CloseChannel()
    {
        if (_view != nullptr) { UnmapViewOfFile(_view); _view = nullptr; }
        if (_map != nullptr) { CloseHandle(_map); _map = nullptr; }
    }

    bool MetricsClient::TryRead(Snapshot& out)
    {
        if (!OpenChannel()) return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            const unsigned char* buffer = _view;
            if (ReadUInt32(buffer, 0) != kMagic)
            {
                // Wrong magic means the mapping was recreated by a different build: reopen.
                CloseChannel();
                return false;
            }

            unsigned int before = ReadUInt32(buffer, 28);
            if ((before & 1u) != 0) continue; // writer mid-copy

            Snapshot parsed;
            parsed.version = ReadUInt16(buffer, 4);
            // v3 appended the per-volume table; an older agent publishing into the same name
            // would leave the tail unreadable, so anything but v3 is treated as "no data".
            if (parsed.version != 3) return false;
            parsed.flags = ReadUInt16(buffer, 6);
            parsed.timestampUtcTicks = ReadInt64(buffer, 8);
            parsed.cpuPercent = ReadSingle(buffer, 16);
            parsed.memoryPercent = ReadSingle(buffer, 20);
            parsed.temperatureC = ReadSingle(buffer, 24);
            parsed.netUpBytesPerSec = ReadDouble(buffer, 32);
            parsed.netDownBytesPerSec = ReadDouble(buffer, 40);
            parsed.memoryUsedBytes = ReadInt64(buffer, 144);
            parsed.memoryTotalBytes = ReadInt64(buffer, 152);
            parsed.cpuUserPercent = ReadSingle(buffer, 160);
            parsed.cpuSystemPercent = ReadSingle(buffer, 164);
            parsed.gpuLoadPercent = ReadSingle(buffer, 236);
            parsed.gpuMemoryPercent = ReadSingle(buffer, 240);
            parsed.gpuTemperatureC = ReadSingle(buffer, 244);
            parsed.diskPercent = ReadSingle(buffer, 248);
            parsed.diskUsedGb = ReadUInt16(buffer, 252);
            parsed.diskTotalGb = ReadUInt16(buffer, 254);
            parsed.diskIoReadBytesPerSec = ReadSingle(buffer, 256);
            parsed.diskIoWriteBytesPerSec = ReadSingle(buffer, 260);

            int volumeCount = ReadUInt16(buffer, 264);
            if (volumeCount > Snapshot::MaxVolumes) volumeCount = Snapshot::MaxVolumes;
            parsed.volumeCount = (unsigned short)volumeCount;
            for (int i = 0; i < volumeCount; i++)
            {
                int slot = 268 + i * 12;
                parsed.volumeLetters[i] = (wchar_t)ReadUInt16(buffer, slot);
                parsed.volumeValid[i] = ReadUInt16(buffer, slot + 2) != 0;
                parsed.volumeRead[i] = ReadSingle(buffer, slot + 4);
                parsed.volumeWrite[i] = ReadSingle(buffer, slot + 8);
            }

            int coreCount = ReadUInt16(buffer, 168);
            if (coreCount > 32) coreCount = 32;
            parsed.coreCount = (unsigned short)coreCount;
            for (int i = 0; i < coreCount; i++)
            {
                parsed.corePercents[i] = buffer[172 + i];
                parsed.coreSystemPercents[i] = buffer[204 + i];
                int user = (int)parsed.corePercents[i] - (int)parsed.coreSystemPercents[i];
                parsed.coreUserPercents[i] = (unsigned char)(user < 0 ? 0 : user);
            }

            for (int i = 0; i < 47; i++)
            {
                wchar_t c = (wchar_t)(buffer[48 + i * 2] | (buffer[48 + i * 2 + 1] << 8));
                if (c == L'\0') break;
                parsed.adapterName[i] = c;
            }

            unsigned int after = ReadUInt32(buffer, 28);
            if (after != before) continue;

            parsed.sequence = after;
            out = parsed;
            return true;
        }
        return false;
    }
}
