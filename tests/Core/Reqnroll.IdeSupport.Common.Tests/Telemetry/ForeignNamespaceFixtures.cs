using System.Runtime.CompilerServices;

// Code outside the Reqnroll.IdeSupport namespace tree, used to prove its frames are never named.
namespace UserCode
{
    internal static class Steps
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Outer() => Inner();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Inner() => throw new InvalidOperationException("user");

        public static Exception ThrowFromForeignTask()
        {
            var task = Task.Run(() => throw new InvalidOperationException("foreign"));
            try { task.Wait(); }
            catch (AggregateException ae) { return ae.InnerException!; }
            throw new InvalidOperationException("no throw");
        }
    }
}

namespace Reqnroll.IdeSupportLookalike
{
    internal static class Evil
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw() => throw new InvalidOperationException("evil");
    }
}
