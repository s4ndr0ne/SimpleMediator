namespace SimpleMediator;

/// <summary>
/// Instructs the SimpleMediator source generator to emit a <see langword="public"/> module registration
/// method in the current assembly so its <see langword="internal"/> handlers and behaviors can be registered
/// from another project without reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class GenerateSimpleMediatorModuleAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateSimpleMediatorModuleAttribute"/> class.
    /// </summary>
    /// <param name="moduleName">
    /// Optional suffix for the generated registration method (<c>PopulateSimpleMediator{ModuleName}Module</c>).
    /// When omitted, the generator uses a sanitized identifier derived from the assembly name.
    /// </param>
    public GenerateSimpleMediatorModuleAttribute(string? moduleName = null)
    {
        ModuleName = moduleName;
    }

    /// <summary>
    /// Gets the optional module name suffix.
    /// </summary>
    public string? ModuleName { get; }
}
