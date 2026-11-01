// Reader side of the shared-memory channel. Must never block or throw: the band runs
// inside Explorer.
#pragma once

#include <windows.h>
#include <string>

namespace pb
{
    enum SnapshotFlags : unsigned short
    {
        FlagCpuValid = 1 << 0,
        FlagMemoryValid = 1 << 1,
        FlagTemperatureValid = 1 << 2,
        FlagNetworkValid = 1 << 3,
        FlagAgentAlive = 1 << 4,
        FlagGpuValid = 1 << 5,
        FlagGpuTemperatureValid = 1 << 6,
        FlagDiskValid = 1 << 7,
        FlagDiskIoValid = 1 << 8
    };

    // Byte-for-byte copy of QuadVitals.Core.MetricsSnapshot (see src/QuadVitals.Core/MetricsSnapshot.cs).
    struct Snapshot
    {
        unsigned int sequence = 0;
        unsigned short version = 0;
        unsigned short flags = 0;
        long long timestampUtcTicks = 0;
        float cpuPercent = 0.0f;
        float memoryPercent = 0.0f;
        float temperatureC = 0.0f;
        double netUpBytesPerSec = 0.0;
        double netDownBytesPerSec = 0.0;
        wchar_t adapterName[48] = { 0 };
        long long memoryUsedBytes = 0;
        long long memoryTotalBytes = 0;
        // CPU split, stored in what used to be the reserved tail (offsets 160 / 164). An older
        // agent leaves them at zero; the renderer then paints the bar in the user colour only.
        float cpuUserPercent = 0.0f;
        float cpuSystemPercent = 0.0f;
        // Per logical processor, offsets 168 (count) and 172 (one byte each). Zero cores means the
        // agent does not publish them; the bar then falls back to the total.
        unsigned short coreCount = 0;
        unsigned char corePercents[64] = { 0 };
        unsigned char coreUserPercents[64] = { 0 };
        unsigned char coreSystemPercents[64] = { 0 };
        // GPU / disk block, offsets 236/240/244/248 (floats) and 252/254 (GB, uint16 each).
        float gpuLoadPercent = 0.0f;
        float gpuMemoryPercent = 0.0f;
        float gpuTemperatureC = 0.0f;
        float diskPercent = 0.0f;
        unsigned short diskUsedGb = 0;
        unsigned short diskTotalGb = 0;
        // v2 tail, offsets 256 / 260: system-wide disk throughput in bytes/s.
        float diskIoReadBytesPerSec = 0.0f;
        float diskIoWriteBytesPerSec = 0.0f;

        // v3 tail, offset 264 (count) / 268 (8 slots of 12 bytes): per-volume throughput for the
        // local drives. Zero count = agent did not sample volumes; per-drive elements show "--".
        static const int MaxVolumes = 8;
        unsigned short volumeCount = 0;
        wchar_t volumeLetters[MaxVolumes] = { 0 };
        bool volumeValid[MaxVolumes] = { false };
        float volumeRead[MaxVolumes] = { 0.0f };
        float volumeWrite[MaxVolumes] = { 0.0f };

        // Finds the slot for a drive letter ('C'); false when the agent did not publish it.
        bool TryVolume(wchar_t letter, bool& valid, double& read, double& write) const;

        bool Has(SnapshotFlags flag) const { return (flags & flag) != 0; }
        // Freshness is decided here, not by the agent: 2 s without a new sample means offline.
        bool IsAlive() const;
        double AgeSeconds() const;
    };

    class MetricsClient
    {
    public:
        ~MetricsClient();

        // Reads one consistent snapshot. Returns false when the agent is not running or the
        // buffer could not be opened; `out` is then untouched.
        bool TryRead(Snapshot& out);

        bool ChannelOpen() const { return _view != nullptr; }

    private:
        bool OpenChannel();
        void CloseChannel();

        HANDLE _map = nullptr;
        const unsigned char* _view = nullptr;
    };
}
