using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Revit.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Refreshes every binding on this object.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

public sealed class Command<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value ? canExecute?.Invoke(value) ?? true : canExecute is null;
    public void Execute(object? parameter)
    {
        if (parameter is T value) execute(value);
    }
}

/// <summary>One item card in a collection brief (01 to 07).</summary>
public sealed class ItemCard(int number, string name, string description) : ObservableObject
{
    private string _name = name;
    private string _description = description;
    public string Number { get; } = number.ToString("D2", CultureInfo.InvariantCulture);
    public string Name { get => _name; set { if (Set(ref _name, value)) Changed?.Invoke(); } }
    public string Description { get => _description; set { if (Set(ref _description, value)) Changed?.Invoke(); } }
    public event Action? Changed;
}

/// <summary>One of a collection's four material notes.</summary>
public sealed class MaterialNote(int number, string text) : ObservableObject
{
    private string _text = text;
    public string Code { get; } = $"MT-{number:D2}";
    public string Text { get => _text; set { if (Set(ref _text, value)) Changed?.Invoke(); } }
    public event Action? Changed;
}

/// <summary>A finish on the review schedule: code, name, where it is used and its colour.</summary>
public sealed class FinishRow : ObservableObject
{
    private string _name;
    private string _description;
    private string _hex;

    public FinishRow(int index, MaterialBrief material)
    {
        Index = index;
        _name = material.Name;
        _description = material.Description;
        _hex = material.Hex;
    }

    public int Index { get; set; }
    public string Code => $"MT-{Index + 1:D2}";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Description { get => _description; set => Set(ref _description, value); }

    public string Hex
    {
        get => _hex;
        set
        {
            if (Set(ref _hex, value)) Raise(nameof(Swatch));
        }
    }

    public Brush Swatch => TryParse(_hex, out var rgb)
        ? new SolidColorBrush(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]))
        : Brushes.Transparent;

    public void Renumber(int index)
    {
        Index = index;
        Raise(nameof(Code));
    }

    public MaterialBrief ToBrief()
    {
        if (!TryParse(_hex, out var rgb)) throw new ArgumentException($"{Code}: use a six-digit colour such as #8A6548.");
        if (string.IsNullOrWhiteSpace(_name)) throw new ArgumentException($"{Code}: give the finish a name.");
        return new MaterialBrief($"m{Index + 1}", _name.Trim(), _description.Trim(), rgb);
    }

    private static bool TryParse(string text, out int[] rgb)
    {
        rgb = Array.Empty<int>();
        var hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        rgb = new[] { (value >> 16) & 255, (value >> 8) & 255, value & 255 };
        return true;
    }
}

/// <summary>A row of the collection schedule: code, name, quantity and overall size.</summary>
public sealed record ScheduleRow(string Code, string Name, string Quantity, string Size, string Finish);

/// <summary>A built family, selectable for loading into a project.</summary>
public sealed class FamilyRow(string assetId, string name, string size, string detail) : ObservableObject
{
    private bool _selected = true;
    public string AssetId { get; } = assetId;
    public string Name { get; } = name;
    public string Size { get; } = size;
    public string Detail { get; } = detail;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed record ActivityRow(string Time, string Text, string Duration, bool IsError);

public sealed record FindingRow(string Tag, string Evidence, string Correction, bool IsMajor);

public sealed record ModelOption(string Id, string Label, string Description, IReadOnlyList<string> Efforts, string DefaultEffort)
{
    public override string ToString() => Label;
}
