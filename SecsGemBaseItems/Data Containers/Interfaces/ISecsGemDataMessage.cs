using SecsGemBaseItems.Data_Containers.Header;
using SecsGemHelperClasses.Copy;

namespace SecsGemBaseItems.Data_Containers.Interfaces;

public interface ISecsGemDataMessage : ITurnToBytes, ICopy<SecsGemDataMessage>, ICanBeParent, IDeepCloneable<ISecsGemDataMessage>
{
    /// <inheritdoc cref="SecsGemDataMessage.reply"/>
    bool Reply { get; set; }

    /// <inheritdoc cref="SecsGemDataMessage.stream"/>
    byte Stream { get; set; }

    /// <inheritdoc cref="SecsGemDataMessage.function"/>
    byte Function { get; set; }

    /// <inheritdoc cref="SecsGemDataMessage.isPrimary"/>
    bool IsPrimary { get; set; }

    /// <inheritdoc cref="SecsGemDataMessage.GetDataFromHeader"/>
    void GetDataFromHeader(HeaderData headerData);
}