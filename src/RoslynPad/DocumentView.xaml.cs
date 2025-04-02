using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Avalon.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynPad.Build;
using RoslynPad.Editor;
using RoslynPad.Themes;
using RoslynPad.UI;

namespace RoslynPad;

public partial class DocumentView : IDisposable
{
    private readonly MarkerMargin _errorMargin;
    private OpenDocumentViewModel? _viewModel;

    public DocumentView()
    {
        InitializeComponent();

        _errorMargin = new MarkerMargin { Visibility = Visibility.Collapsed, MarkerImage = TryFindResource("ExceptionMarker") as ImageSource, Width = 10 };
        Editor.TextArea.LeftMargins.Insert(0, _errorMargin);
        Editor.PreviewMouseWheel += EditorPreviewMouseWheel;
        Editor.TextArea.Caret.PositionChanged += CaretOnPositionChanged;
        Editor.TextArea.SelectionChanged += EditorSelectionChanged;

        DataContextChanged += OnDataContextChanged;


        //TODO: Add AvalonEditCommands ToggleAllFolds, ToggleFold
        //CommandBindings.Add(new CommandBinding(AvalonEditCommands.ToggleAllFolds, (s, e) => ToggleAllFoldings()));
        //CommandBindings.Add(new CommandBinding(AvalonEditCommands.ToggleFold, (s, e) => ToggleCurrentFolding()));
    }

    public OpenDocumentViewModel ViewModel => _viewModel.NotNull();

    private void EditorSelectionChanged(object? sender, EventArgs e)
        => ViewModel.SelectedText = Editor.SelectedText;

    private void CaretOnPositionChanged(object? sender, EventArgs eventArgs)
    {
        Ln.Text = Editor.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
        Col.Text = Editor.TextArea.Caret.Column.ToString(CultureInfo.InvariantCulture);
    }

    private void EditorPreviewMouseWheel(object? sender, MouseWheelEventArgs args)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.MainViewModel.EditorFontSize += args.Delta > 0 ? 1 : -1;
            args.Handled = true;
        }
    }

    private async void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs args)
    {
        _viewModel = (OpenDocumentViewModel)args.NewValue;
        BindingOperations.EnableCollectionSynchronization(_viewModel.Results, _viewModel.Results);

        _viewModel.ReadInput += OnReadInput;
        _viewModel.NuGet.PackageInstalled += NuGetOnPackageInstalled;

        _viewModel.EditorFocus += (o, e) => Editor.Focus();
        _viewModel.EditorChangeLocation += ((int line, int column) value) => ChangePosition(value.line, value.column);
        _viewModel.DocumentUpdated += (o, e) => 
        {
            Dispatcher.InvokeAsync(() => Editor.RefreshHighlighting());
            Dispatcher.InvokeAsync(() => Editor.RefreshFoldings());            
        };

        _viewModel.MainViewModel.EditorFontSizeChanged += EditorFontSizeChanged;
        Editor.FontSize = _viewModel.MainViewModel.EditorFontSize;

        var documentText = await _viewModel.LoadTextAsync().ConfigureAwait(true);

        ViewModel.MainViewModel.ThemeChanged += OnThemeChanged;
        var documentId = await Editor.InitializeAsync(_viewModel.MainViewModel.RoslynHost, new ThemeClassificationColors(_viewModel.MainViewModel.Theme),
            _viewModel.WorkingDirectory, documentText, _viewModel.SourceCodeKind).ConfigureAwait(true);

        _viewModel.Initialize(documentId, OnError,
            () => new TextSpan(Editor.SelectionStart, Editor.SelectionLength),
            this);

        Editor.Document.TextChanged += (o, e) => _viewModel.OnTextChanged();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Editor.ClassificationHighlightColors = new ThemeClassificationColors(ViewModel.MainViewModel.Theme);
    }

    private void OnReadInput()
    {
        var textBox = new TextBox();

        var dialog = new TaskDialog
        {
            Header = "Console Input",
            Content = textBox,
            Background = Brushes.White,
        };

        textBox.Loaded += (o, e) => textBox.Focus();

        textBox.KeyDown += (o, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TaskDialog.CancelCommand.Execute(null, dialog);
            }
        };

        dialog.ShowInline(this);

        ViewModel.SendInput(textBox.Text);
    }

    private void OnError(ExceptionResultObject? e)
    {
        if (e != null)
        {
            _errorMargin.Visibility = Visibility.Visible;
            _errorMargin.LineNumber = e.LineNumber;
            _errorMargin.Message = "Exception: " + e.Message;
        }
        else
        {
            _errorMargin.Visibility = Visibility.Collapsed;
        }
    }

    private void EditorFontSizeChanged(double fontSize)
    {
        Editor.FontSize = fontSize;
    }

    private void NuGetOnPackageInstalled(PackageData package)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var text = $"#r \"nuget: {package.Id}, {package.Version}\"{Environment.NewLine}";
            Editor.Document.Insert(0, text, AnchorMovementType.Default);
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.T:
                    e.Handled = true;
                    NuGetSearch.Focus();
                    break;
            }
        }
    }

    private void Editor_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(Editor.Focus, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_viewModel?.MainViewModel is not { } mainViewModel)
        {
            return;
        }

        mainViewModel.EditorFontSizeChanged -= EditorFontSizeChanged;
        mainViewModel.ThemeChanged -= OnThemeChanged;
    }

    private void ChangePosition(int lineNumber, int column)
    {
        Editor.TextArea.Caret.Line = lineNumber;
        Editor.TextArea.Caret.Column = column;
        Editor.ScrollToLine(lineNumber);

        _ = Dispatcher.InvokeAsync(Editor.Focus);
    }

    private void SearchTerm_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ViewModel.NuGet.Packages?.Any() == true)
        {
            if (!ViewModel.NuGet.IsPackagesMenuOpen)
            {
                ViewModel.NuGet.IsPackagesMenuOpen = true;
            }
            RootNuGetMenu.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Editor.Focus();
        }
    }

    ////////////////////////////////////////// GREYWIND

    private async void BuildRunBtn_Click(object sender, RoutedEventArgs e)
    {
        // 清空之前的输出
        OutputTextBox.Text = string.Empty;

        try
        {
            string fileName = SearchProgramFile()!;
            string dllFilePath = Path.Combine(_viewModel!.BuildPath, "bin");
            string dllFile = Directory.GetFiles(dllFilePath, "*.dll").Single(f => !Path.GetFileName(f).Equals("RoslynPad.Runtime.dll", StringComparison.OrdinalIgnoreCase));
            //string dllFile = fileName + ".dll";

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",  // 直接运行dotnet程序
                Arguments = $"\"{dllFile}\"",  // 处理路径中的空格
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;

                // 异步输出处理
                process.OutputDataReceived += (sender, e) => AppendOutputText(e.Data!);
                process.ErrorDataReceived += (sender, e) => AppendOutputText(e.Data!);

                process.Start();

                // 开始异步读取输出
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 异步等待进程退出（需要.NET 5+）
                await process.WaitForExitAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Search DLL file error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        void AppendOutputText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // WPF跨线程访问UI控件
            if (!OutputTextBox.Dispatcher.CheckAccess())
            {
                OutputTextBox.Dispatcher.InvokeAsync(() =>
                    OutputTextBox.AppendText($"{text}{Environment.NewLine}"));
            }
            else
            {
                OutputTextBox.AppendText($"{text}{Environment.NewLine}");
            }
        }
    }

    private string? SearchProgramFile()
    {
        try
        {
            // 增加目录存在性校验
            if (!Directory.Exists(_viewModel!.BuildPath))
            {
                Console.WriteLine("目录不存在");
                return null;
            }

            // 获取首个符合条件的文件名（不包含扩展名）
            string firstFileName = Directory.EnumerateFiles(_viewModel!.BuildPath, "*.cs")
                .Where(file =>
                    !Path.GetFileName(file).Equals("ModuleInitializer.cs", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileNameWithoutExtension(file)) // 直接移除扩展名
                .FirstOrDefault()!;

            return firstFileName;
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "Search DLL file error", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }
}
