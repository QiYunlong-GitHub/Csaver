using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using Csaver.Core;

namespace Csaver.App;

public sealed class SelectableTask : INotifyPropertyChanged
{
    private bool isSelected;
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public string Guard { get; init; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public bool Enabled { get; init; } = true;
    public bool IsSelected { get => isSelected; set { isSelected = value; OnPropertyChanged(); } }
    public string SizeDisplay => SizeBytes is null ? "Unknown" : FormatBytes(SizeBytes.Value);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value; var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.##} {units[index]}";
    }
}

public sealed record CommandChoice(OperationKind Kind, string Name, string Description, bool Mutating, bool UsesTasks = true);

// History keeps UTC DateTimeOffset values for stable filenames, sorting, rollback
// authorization, and CSV exchange. Only the History grid renders them as local time.
public sealed class LocalTimeConverter : IValueConverter
{
    private const string DisplayFormat = "yyyy-MM-dd HH:mm:ss";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value switch
            {
                DateTimeOffset offset => offset.ToLocalTime().ToString(DisplayFormat, CultureInfo.InvariantCulture),
                DateTime dateTime => dateTime.ToLocalTime().ToString(DisplayFormat, CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? string.Empty
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            // Imported manifests may carry DateTimeOffset.MinValue, which cannot be
            // shifted into every local time zone. Show the stored value instead.
            return value is DateTimeOffset offset
                ? offset.ToString(DisplayFormat, CultureInfo.InvariantCulture)
                : value?.ToString() ?? string.Empty;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("History timestamps are read-only.");
}
