namespace SecsGemBaseItems.Data_Containers.Interfaces;

public interface ICanBeParent
{
    bool CanAddChild(IDataItem? child);
    bool TryAddChild(IDataItem child);
    bool TryRemoveChild(IDataItem child);
}
