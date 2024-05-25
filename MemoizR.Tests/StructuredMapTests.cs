using Xunit;

namespace MemoizR.Tests.StructuredConcurrency
{
    public class ConcurrentMapTests
    {
        [Fact]
        public async Task TestConcurrentMap()
        {
            var f = new MemoFactory();

            var v1 = f.CreateSignal(1);
            var v2 = f.CreateSignal(2);
            var v3 = f.CreateSignal(3);

            var map = f.CreateConcurrentMap(
                async (cts) => await v1.Get(),
                async (cts) => await v2.Get(),
                async (cts) => await v3.Get());

            Assert.Equal(new[] { 1, 2, 3 }, await map.Get());

            await v1.Set(4);
            Assert.Equal(new[] { 4, 2, 3 }, await map.Get());

            await v2.Set(5);
            Assert.Equal(new[] { 4, 5, 3 }, await map.Get());

            await v3.Set(6);
            Assert.Equal(new[] { 4, 5, 6 }, await map.Get());
        }

        [Fact]
        public async Task TestConcurrentMapWithDiamondDependencies()
        {
            var f = new MemoFactory();

            var v1 = f.CreateSignal(1);
            var v2 = f.CreateSignal(2);
            var v3 = f.CreateSignal(3);

            var map1 = f.CreateConcurrentMap(
                async (cts) => await v1.Get(),
                async (cts) => await v2.Get(),
                async (cts) => await v3.Get());

            var map2 = f.CreateConcurrentMap(
                async (cts) => await map1.Get(),
                async (cts) => await map1.Get());

            var map3 = f.CreateConcurrentMap(
                async (cts) => await map2.Get(),
                async (cts) => await map2.Get());

            Assert.Equal(new[] { 1, 2, 3 }, await map1.Get());

            await v1.Set(4);
            Assert.Equal(new[] { 4, 2, 3 }, await map1.Get());

            await v2.Set(5);
            Assert.Equal(new[] { 4, 5, 3 }, await map1.Get());

            await v3.Set(6);
            Assert.Equal(new[] { 4, 5, 6 }, await map1.Get());
        }
    }
}