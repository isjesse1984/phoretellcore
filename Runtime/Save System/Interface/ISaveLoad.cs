namespace Phoretell
{
    /// <summary>
    /// Implement this interface on any scene component that contributes to a
    /// serializable data type. The save system discovers implementations at runtime.
    /// </summary>
    /// <typeparam name="TData">
    /// A project-owned, serializable class with a parameterless constructor.
    /// </typeparam>
    public interface ISaveLoad<TData>
    {
        void SaveData(TData data);
        void LoadData(TData data);
    }

    /// <summary>
    /// Optional override for the file key used by an ISaveLoad implementation.
    /// Most projects can rely on the data type's full name instead.
    /// </summary>
    public interface ISaveKeyProvider
    {
        string SaveKey { get; }
    }
}
