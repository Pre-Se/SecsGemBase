namespace SecsGemHelperClasses.Copy;

public interface ICopy<T>
{
    public void CopyFrom(T source);
    T GetCopySource();
}