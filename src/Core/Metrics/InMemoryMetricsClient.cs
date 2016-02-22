﻿using System;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : CacheBucketMetricsClientBase {
        public InMemoryMetricsClient(bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null) : base(new InMemoryCacheClient(loggerFactory), buffered, prefix, loggerFactory) {}
    }
}
