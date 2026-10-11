using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using JavaSelect.Core;
using JavaSelect.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JavaSelect.App.UI;

public sealed class JavaRow : INotifyPropertyChanged
{
    private JavaInstallation java = new();
    private Localizer text = new("zh-CN");
    private bool selected, protectedSelection;
    private string? iconPath;
    public string Id => java.Id;
    public JavaInstallation Installation => java;
    public bool Switching { get; }
    public string Name => java.DisplayName;
    public string Detail => $"{java.Version}  ·  {java.Kind}  ·  {java.Vendor}  ·  {java.Architecture}";
    public string Path => java.Path;
    public string Badge => !java.IsValid ? text["unavailable"] : text["selected"];
    public string SwitchLabel => text["use"];
    public string EditLabel => text["edit"];
    public string RemoveLabel => text["remove"];
    public bool CanSwitch => java.IsValid && !selected;
    public bool CanRemove => !protectedSelection;
    public ImageSource? Icon { get; private set; }
    public Visibility ImageVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => !java.IsValid || selected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SwitchVisibility => Switching ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManageVisibility => Switching ? Visibility.Collapsed : Visibility.Visible;
    public ICommand SwitchCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public JavaRow(bool switching, Func<JavaInstallation, Task> change, Func<JavaInstallation, Task> edit, Func<JavaInstallation, Task> remove)
    {
        Switching = switching;
        SwitchCommand = new AsyncCommand(() => change(java));
        EditCommand = new AsyncCommand(() => edit(java));
        RemoveCommand = new AsyncCommand(() => remove(java));
    }

    public void Update(JavaInstallation value, string? current, string? last, Localizer localizer)
    {
        if (iconPath != value.IconPath || Icon is null)
        {
            Icon = !string.IsNullOrEmpty(value.IconPath) && File.Exists(value.IconPath) ? new BitmapImage(new Uri(value.IconPath)) : null;
            iconPath = value.IconPath;
        }
        java = value; text = localizer; selected = current == java.Id; protectedSelection = last == java.Id;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed class AsyncCommand(Func<Task> action) : ICommand
{
    private bool executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !executing;
    public async void Execute(object? parameter)
    {
        if (executing) return;
        executing = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await action(); }
        finally { executing = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
