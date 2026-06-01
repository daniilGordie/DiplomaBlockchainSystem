using System;
using System.Collections.Concurrent;
using System.Linq;
using Blockchain.Application.Git;

namespace Blockchain.Node.Services
{
    public sealed class WebhookReplayGuard : IRequestReplayGuard
    {
        private readonly ConcurrentDictionary<string, DateTime> _processed = new();
        private readonly TimeSpan _ttl;

        public WebhookReplayGuard(TimeSpan? ttl = null)
        {
            _ttl = ttl ?? TimeSpan.FromHours(6);
        }

        public bool TryRegister(string key, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!_processed.TryAdd(key, nowUtc))
            {
                return false;
            }

            SweepExpired(nowUtc);
            return true;
        }

        public int SweepExpired(DateTime nowUtc)
        {
            int removed = 0;
            foreach (var item in _processed.Where(x => (nowUtc - x.Value) > _ttl).ToList())
            {
                if (_processed.TryRemove(item.Key, out _))
                {
                    removed++;
                }
            }

            return removed;
        }
    }
}
