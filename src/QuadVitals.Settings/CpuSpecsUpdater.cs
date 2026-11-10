using System;
using System.Threading.Tasks;
using QuadVitals.Core;

namespace QuadVitals.Settings
{
    /// <summary>
    /// 启动时把处理器规格表补上：先读本地缓存，缓存过期或缺失再拉一次远端。
    /// 全程后台线程，失败不弹窗也不写日志——规格表只用来在没有固件值时给两个固定值，
    /// 拿不到就继续用包里那份小表，仪表盘该显示的照样显示。
    /// </summary>
    internal static class CpuSpecsUpdater
    {
        private static bool _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            Task.Run(() =>
            {
                try
                {
                    string cache = CpuSpecs.CachePath();
                    string error;
                    // 先装上包里那份基线，离线新装也能拿到完整列表；缓存与远端只负责覆盖它。
                    CpuSpecs.TryLoadEmbedded(out error);
                    CpuSpecs.TryLoadCache(cache, out error);
                    if (CpuSpecs.CacheIsFresh(cache, DateTime.UtcNow, CpuSpecs.MaxCacheAge))
                    {
                        return;
                    }

                    CpuSpecs.Refresh(new HttpUpdateTransport(), CpuSpecs.FeedUrl(), cache,
                        DateTime.UtcNow, CpuSpecs.MaxCacheAge, CpuSpecs.DefaultTimeout, out error);
                }
                catch (Exception)
                {
                    // 网络、代理、DNS 都可能失败；这里不是错误路径，只是没拿到更新而已。
                }
            });
        }
    }
}
