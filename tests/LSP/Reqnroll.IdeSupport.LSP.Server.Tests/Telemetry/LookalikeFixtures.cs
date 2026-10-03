using System.Runtime.CompilerServices;

// Namespace that merely starts with "Reqnroll.IdeSupport" (no dot): must never be treated as product code.
namespace Reqnroll.IdeSupportLookalike
{
    internal static class Evil
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw() => throw new InvalidOperationException("evil");
    }
}
