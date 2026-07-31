namespace SecsGemHelperClasses;

public static class MessageHandlingHelpers
{
    public static void InsertReversedDataIntoArray(byte[] destination, byte[] source, uint destinationIndex = 0, uint sourceIndex = 0, int lenght = 0)
    {
            if (lenght <= 0)
            {
                lenght = source.Length;
            }
            Array.Copy(Reverse(source, lenght), sourceIndex, destination, destinationIndex, lenght);

    }
    public static byte[] Reverse(byte[] data, int length = 0, int index = 0)
    {
            if (length <= 0) length = data.Length;
            if (length == 1) return [data[index]];
            byte[] copy = new byte[length];
            Array.Copy(data, index, copy, 0, length);
            Array.Reverse(copy);
            return copy;
    }
    public static byte[] Combine(params byte[][] arrays)
    {
            byte[] ret = new byte[arrays.Sum(x => x.Length)];
            int offset = 0;
            foreach (byte[] data in arrays)
            {
                Buffer.BlockCopy(data, 0, ret, offset, data.Length);
                offset += data.Length;
            }
            return ret;
    }
    public static byte[] AddPaddingBytes(byte[] sourceBytes, int paddingSize)
    {
            byte[] paddedBytes = new byte[paddingSize];
            Array.Copy(sourceBytes, 0, paddedBytes, 0, sourceBytes.Length);
            return paddedBytes;
    }
}