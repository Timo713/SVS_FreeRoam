using System;
using BepInEx.Configuration;

/// <summary>
/// Passed as a tag on a <see cref="ConfigDescription"/> to change how ConfigurationManager
/// draws one setting.
///
/// This class is deliberately in the global namespace with public fields: ConfigurationManager
/// matches it **by field name through reflection**, not by type, so any plugin can declare its
/// own copy. Nothing here references ConfigurationManager, so it costs nothing when that plugin
/// is not installed — the tag is then just an object nobody reads.
///
/// It affects only how our own settings appear inside ConfigurationManager's window. It cannot
/// touch other plugins, their settings, or the game.
///
/// Only the fields actually used are declared; the real class has more.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>Draw this setting yourself instead of letting ConfigurationManager do it.</summary>
    public Action<ConfigEntryBase> CustomDrawer;

    /// <summary>False hides the setting from the window entirely. Still editable in the .cfg.</summary>
    public bool? Browsable;

    /// <summary>Overrides the label shown to the left of the setting.</summary>
    public string DispName;

    /// <summary>Lower numbers sort further down within a section.</summary>
    public int? Order;

    /// <summary>True hides ConfigurationManager's own Reset button for this setting.</summary>
    public bool? HideDefaultButton;

    /// <summary>Show only the drawer, across the whole row, without the setting's name.</summary>
    public bool? HideSettingName;

}
