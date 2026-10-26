using System.Reflection;
using Windows.Storage.Pickers;
using DbSchemaSyncAuditor.Model;
using DbSchemaSyncAuditor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace DbSchemaSyncAuditor;

public sealed partial class MainPage : Page
{
    private readonly SqlServerSchemaReader _reader = new();
    private readonly EfModelInspector _inspector = new();
    private readonly SchemaDiffEngine _diffEngine = new();
    private readonly SqlPatchGenerator _patchGen = new();
    private readonly SqlScriptRunner _runner = new();

    private List<SchemaDiffItem> _currentDiffs = [];
    private string _generatedSql = string.Empty;
    private List<Type> _discoveredDbContextTypes = [];
    private string? _selectedDllPath;

    public MainPage()
    {
        InitializeComponent();
    }

    private void CboAuthType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUser == null || TxtPassword == null) return;
        var isSqlAuth = CboAuthType.SelectedIndex == 0;
        TxtUser.IsEnabled = isSqlAuth;
        TxtPassword.IsEnabled = isSqlAuth;
    }

    private async void BtnConnectServer_Click(object sender, RoutedEventArgs e)
    {
        var server = TxtServer.Text.Trim();
        var isSqlAuth = CboAuthType.SelectedIndex == 0;
        var user = TxtUser.Text.Trim();
        var password = TxtPassword.Password;

        if (string.IsNullOrWhiteSpace(server))
        {
            await ShowMessageAsync("Validación", "Por favor ingresa el nombre o dirección del servidor SQL.");
            return;
        }

        try
        {
            BtnConnectServer.IsEnabled = false;
            RingConnecting.Visibility = Visibility.Visible;
            RingConnecting.IsActive = true;
            TxtFooterStatus.Text = $"Conectando a {server}...";

            var dbs = await _reader.GetDatabasesAsync(server, user, password, !isSqlAuth);

            CboDatabases.Items.Clear();
            foreach (var db in dbs)
            {
                CboDatabases.Items.Add(db);
            }

            if (dbs.Count > 0)
            {
                CboDatabases.SelectedIndex = 0;
                TxtFooterStatus.Text = $"Conexión exitosa. Se encontraron {dbs.Count} bases de datos.";
            }
            else
            {
                TxtFooterStatus.Text = "Conectado, pero no se encontraron bases de datos de usuario.";
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error de Conexión", $"No fue posible conectar al servidor SQL:\n{ex.Message}");
            TxtFooterStatus.Text = "Falló la conexión al servidor.";
        }
        finally
        {
            BtnConnectServer.IsEnabled = true;
            RingConnecting.IsActive = false;
            RingConnecting.Visibility = Visibility.Collapsed;
        }
    }

    private void TxtDllPathInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var path = TxtDllPathInput.Text.Trim();
        if (System.IO.File.Exists(path))
        {
            LoadAssemblyFromPath(path, silent: true);
        }
    }

    private async void BtnBrowseDll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".dll");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                TxtDllPathInput.Text = file.Path;
                LoadAssemblyFromPath(file.Path, silent: false);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Aviso", $"No se pudo abrir el selector: {ex.Message}.\nPuedes escribir o pegar la ruta directamente en la caja de texto.");
        }
    }

    private async void LoadAssemblyFromPath(string path, bool silent)
    {
        try
        {
            _selectedDllPath = path;
            _discoveredDbContextTypes = _inspector.DiscoverDbContextTypesFromAssembly(_selectedDllPath);

            CboDbContextTypes.Items.Clear();
            foreach (var type in _discoveredDbContextTypes)
            {
                CboDbContextTypes.Items.Add(type.FullName ?? type.Name);
            }

            if (_discoveredDbContextTypes.Count > 0)
            {
                CboDbContextTypes.SelectedIndex = 0;
                CboDbContextTypes.IsEnabled = true;
                if (!silent)
                {
                    await ShowMessageAsync("DbContext(s) Detectados", $"Se cargó el archivo correctamente:\n{System.IO.Path.GetFileName(path)}\n\nDbContexts detectados: {_discoveredDbContextTypes.Count}");
                }
            }
            else
            {
                CboDbContextTypes.IsEnabled = false;
                if (!silent)
                {
                    await ShowMessageAsync("Aviso", "El ensamblado .DLL no contiene clases públicas que hereden de 'DbContext'.");
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                await ShowMessageAsync("Error al inspeccionar ensamblado", $"{ex.GetType().Name}:\n{ex.Message}");
            }
        }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        var pathFromBox = TxtDllPathInput.Text.Trim();
        if (_discoveredDbContextTypes.Count == 0 && System.IO.File.Exists(pathFromBox))
        {
            LoadAssemblyFromPath(pathFromBox, silent: true);
        }

        // 1. VALIDACIÓN ESTRICTA DE DLL Y DBCONTEXT
        if (string.IsNullOrWhiteSpace(_selectedDllPath) || _discoveredDbContextTypes.Count == 0 || CboDbContextTypes.SelectedIndex < 0)
        {
            await ShowMessageAsync("DbContext no seleccionado", 
                "Verifica la ruta de tu DLL (ej: D:\\Dev\\PROYECTO LACAK WEB\\DashboardWEB\\dashboardWEB\\bin\\LacakWEB.dll).\n\nAsegúrate de que el archivo exista y se haya detectado el DbContext.");
            return;
        }

        var server = TxtServer.Text.Trim();
        var isSqlAuth = CboAuthType.SelectedIndex == 0;
        var user = TxtUser.Text.Trim();
        var password = TxtPassword.Password;
        var selectedDb = CboDatabases.SelectedItem as string;
        var clientName = TxtClientName.Text.Trim();

        if (string.IsNullOrWhiteSpace(server))
        {
            await ShowMessageAsync("Validación", "Ingresa el nombre del servidor SQL.");
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedDb))
        {
            await ShowMessageAsync("Validación", "Por favor haz clic en 'Conectar y Cargar BDs' y selecciona la base de datos a auditar.");
            return;
        }

        var connString = _reader.BuildConnectionString(server, selectedDb, user, password, !isSqlAuth);
        var selectedDbContextType = _discoveredDbContextTypes[CboDbContextTypes.SelectedIndex];

        try
        {
            BtnScan.IsEnabled = false;
            BtnApplyMigration.IsEnabled = false;
            RingLoading.Visibility = Visibility.Visible;
            RingLoading.IsActive = true;
            TxtSummary.Text = $"Extrayendo modelo de '{selectedDbContextType.Name}' y auditando '{selectedDb}'...";

            // 1. Extraer modelo relacional desde el DbContext seleccionado
            var efSchema = _inspector.ExtractModelFromAnyDbContext(selectedDbContextType, connString);

            // 2. Extraer catálogo físico de SQL Server
            var dbSchema = await _reader.ReadDatabaseSchemaAsync(connString);

            // 3. Comparar
            _currentDiffs = _diffEngine.Compare(dbSchema, efSchema);
            ListDiffs.ItemsSource = _currentDiffs;

            var actionable = _currentDiffs.Where(d => d.Type != DiffType.ExtraInDb).ToList();
            BadgeStatus.Value = actionable.Count;

            if (actionable.Count == 0)
            {
                TxtSummary.Text = $"✔ La base de datos '{selectedDb}' está 100% sincronizada con '{selectedDbContextType.Name}'.";
                TxtFooterStatus.Text = "No se detectaron diferencias.";
                TxtGeneratedSql.Text = "-- No hay cambios pendientes. Esquema sincronizado.";
                BtnApplyMigration.IsEnabled = false;
            }
            else
            {
                TxtSummary.Text = $"Se detectaron {actionable.Count} diferencias en '{selectedDb}' respecto a '{selectedDbContextType.Name}'.";
                TxtFooterStatus.Text = $"{actionable.Count} cambio(s) listos para migrar.";

                _generatedSql = _patchGen.GeneratePatchScript(actionable, string.IsNullOrWhiteSpace(clientName) ? selectedDb : clientName);
                TxtGeneratedSql.Text = _generatedSql;
                BtnApplyMigration.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error durante el escaneo", ex.Message);
            TxtSummary.Text = "Falló el escaneo.";
            TxtFooterStatus.Text = "Error en la inspección de esquemas.";
        }
        finally
        {
            BtnScan.IsEnabled = true;
            RingLoading.IsActive = false;
            RingLoading.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnSaveScript_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_generatedSql))
        {
            await ShowMessageAsync("Aviso", "No hay script SQL generado para guardar.");
            return;
        }

        try
        {
            var clientName = string.IsNullOrWhiteSpace(TxtClientName.Text) ? "Cliente" : TxtClientName.Text.Trim();
            var fileName = $"patch_{clientName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sql";
            var fullPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(fullPath, _generatedSql);

            TxtScriptPath.Text = $"Guardado en: {fullPath}";
            await ShowMessageAsync("Éxito", $"Script guardado en:\n{fullPath}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error al guardar", ex.Message);
        }
    }

    private async void BtnApplyMigration_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_generatedSql)) return;

        var selectedDb = CboDatabases.SelectedItem as string ?? "Base de Datos";

        var confirmDialog = new ContentDialog
        {
            Title = "Confirmar Ejecución Transaccional",
            Content = $"¿Deseas aplicar estos cambios automáticamente en '{selectedDb}' ahora?\n\nEsta operación se ejecutará dentro de una única transacción con ROLLBACK automático en caso de error.",
            PrimaryButtonText = "Sí, Aplicar Ahora",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            BtnApplyMigration.IsEnabled = false;
            BtnScan.IsEnabled = false;
            TxtFooterStatus.Text = $"Ejecutando script en '{selectedDb}'...";
            ResultsTabView.SelectedIndex = 2; // Pestaña de logs

            TxtExecutionLogs.Text = $"Iniciando transacción en [{selectedDb}]...\n";

            var server = TxtServer.Text.Trim();
            var isSqlAuth = CboAuthType.SelectedIndex == 0;
            var user = TxtUser.Text.Trim();
            var password = TxtPassword.Password;
            var connString = _reader.BuildConnectionString(server, selectedDb, user, password, !isSqlAuth);

            var (success, logs) = await _runner.ExecuteScriptAsync(connString, _generatedSql);
            TxtExecutionLogs.Text += logs;

            if (success)
            {
                TxtExecutionLogs.Text += "\n✔ Migración aplicada con éxito.";
                TxtFooterStatus.Text = "Migración completada exitosamente.";
                await ShowMessageAsync("Éxito", $"La base de datos '{selectedDb}' fue actualizada satisfactoriamente.");

                // Re-escanear para validar sincronización
                BtnScan_Click(this, new RoutedEventArgs());
            }
            else
            {
                TxtExecutionLogs.Text += "\n✖ La migración falló y se aplicó ROLLBACK.";
                TxtFooterStatus.Text = "Falló la migración. Revisa los logs.";
                await ShowMessageAsync("Error", "Ocurrió un error al ejecutar la migración. Se aplicó ROLLBACK.");
            }
        }
        catch (Exception ex)
        {
            TxtExecutionLogs.Text += $"\n[ERROR FATAL] {ex.Message}";
            await ShowMessageAsync("Error de ejecución", ex.Message);
        }
        finally
        {
            BtnScan.IsEnabled = true;
        }
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Aceptar",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
