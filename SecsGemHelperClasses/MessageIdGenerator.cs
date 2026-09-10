using System.Runtime.CompilerServices;

namespace SecsGemHelperClasses;

public static class MessageIdGenerator
{
    private static int _id = Random.Shared.Next();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint NewId() => (uint)Interlocked.Increment(ref _id);
}