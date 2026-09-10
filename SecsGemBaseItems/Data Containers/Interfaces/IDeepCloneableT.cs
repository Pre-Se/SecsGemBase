namespace SecsGemBaseItems.Data_Containers.Interfaces;

public interface IDeepCloneable<out T>
{
    public T Clone();
}
