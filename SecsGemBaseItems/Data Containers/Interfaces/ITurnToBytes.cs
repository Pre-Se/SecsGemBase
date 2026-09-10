namespace SecsGemBaseItems.Data_Containers.Interfaces;

public interface ITurnToBytes
{
    ReadOnlyMemory<byte> ToBytes();
}