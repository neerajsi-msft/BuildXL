// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Cache.Interfaces;
using BuildXL.Cache.MemoizationStoreAdapter;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;

namespace BuildXL.Cache.Tests
{
    public class MemoizationStoreAdapterLocalCacheTests : TestMemoizationStoreAdapterLocalCacheBase
    {
        [Theory]
        [InlineData(null, 600)]
        [InlineData(12, 12)]
        [InlineData(0, 0)]
        [InlineData(-1, -1)]
        public void ParseFlushInterval(int? configuredSeconds, int expectedSeconds)
        {
            var json = NewCache(nameof(ParseFlushInterval), true);
            if (configuredSeconds.HasValue)
            {
                json = json.Insert(json.LastIndexOf('}'), $", \"RocksDbMemoizationStoreFlushIntervalInSeconds\": {configuredSeconds.Value}");
            }

            XAssert.IsTrue(CacheFactory.TryCreateCacheConfigData(json, out var cacheData, out var exception));
            XAssert.IsNull(exception);
            var configuration = cacheData.Create<MemoizationStoreCacheFactory.Config>();
            XAssert.IsTrue(configuration.Succeeded);
            XAssert.AreEqual(expectedSeconds, configuration.Result.RocksDbMemoizationStoreFlushIntervalInSeconds);
        }

        protected override string DefaultMemoizationStoreJsonConfigString => @"{{
            ""Assembly"":""BuildXL.Cache.MemoizationStoreAdapter"",
            ""Type"":""BuildXL.Cache.MemoizationStoreAdapter.MemoizationStoreCacheFactory"",
            ""CacheId"":""{0}"",
            ""MaxCacheSizeInMB"":{1},
            ""MaxStrongFingerprints"":{2},
            ""CacheRootPath"":""{3}"",
            ""CacheLogPath"":""{4}"",
            ""SingleInstanceTimeoutInSeconds"":10,
            ""ApplyDenyWriteAttributesOnContent"":""true""
        }}";
    }
}
