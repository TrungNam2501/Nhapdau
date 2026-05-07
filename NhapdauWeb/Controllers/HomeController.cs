using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using NhapdauWeb.Models;

namespace NhapdauWeb.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly string _connectionString;
    private readonly string _erpConnectionString;
    private readonly string _erpServer34ConnectionString;

    public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("Server33")
            ?? throw new InvalidOperationException("Connection string 'Server33' not found.");
        _erpConnectionString = configuration.GetConnectionString("Server33Erp")
            ?? throw new InvalidOperationException("Connection string 'Server33Erp' not found.");
        _erpServer34ConnectionString = configuration.GetConnectionString("Server34Erp")
            ?? throw new InvalidOperationException("Connection string 'Server34Erp' not found.");
    }

    public async Task<IActionResult> Index(string? selectedBarcode)
    {
        var viewModel = new OilViewModel
        {
            SelectedBarcode = selectedBarcode,
            ErrorMessage = TempData["ErrorMessage"] as string,
            SuccessMessage = TempData["SuccessMessage"] as string
        };

        await LoadBarcodeList(viewModel, selectedBarcode);

        if (!string.IsNullOrEmpty(selectedBarcode))
        {
            await LoadOilRecords(viewModel, selectedBarcode);
        }

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Insert(string selectedBarcode, string newHmiBarcode)
    {
        if (string.IsNullOrEmpty(selectedBarcode))
        {
            TempData["ErrorMessage"] = "Vui lòng chọn Barcode trước khi nhập mới.";
            return RedirectToAction("Index", new { selectedBarcode });
        }

        if (string.IsNullOrWhiteSpace(newHmiBarcode))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập HMI Barcode.";
            return RedirectToAction("Index", new { selectedBarcode });
        }

        if (!newHmiBarcode.StartsWith(selectedBarcode, StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = $"HMI Barcode phải bắt đầu bằng '{selectedBarcode}'. Giá trị nhập: '{newHmiBarcode}'";
            return RedirectToAction("Index", new { selectedBarcode });
        }

        // Kiểm tra HMI Barcode đã được nghiệm thu trong erp.dbo.prdgdt chưa (Server33)
        using (var erpConnection = new SqlConnection(_erpConnectionString))
        {
            await erpConnection.OpenAsync();
            using var cmdPrdgdt = new SqlCommand(
                "SELECT COUNT(1) FROM [erp].[dbo].[prdgdt] WHERE RTRIM([slipno]) = @hmiBarcode",
                erpConnection);
            cmdPrdgdt.Parameters.AddWithValue("@hmiBarcode", newHmiBarcode);
            var prdgdtCount = (int)(await cmdPrdgdt.ExecuteScalarAsync() ?? 0);
            if (prdgdtCount == 0)
            {
                TempData["ErrorMessage"] = $"HMI Barcode '{newHmiBarcode}' chưa nghiệm thu.";
                return RedirectToAction("Index", new { selectedBarcode });
            }
        }

        // Tách HMI Barcode: 3 ký tự cuối = seqno, phần còn lại = barcode
        if (newHmiBarcode.Length < 4)
        {
            TempData["ErrorMessage"] = $"HMI Barcode '{newHmiBarcode}' không hợp lệ để tách barcode/seqno (độ dài tối thiểu 4 ký tự).";
            return RedirectToAction("Index", new { selectedBarcode });
        }
        var gdtBarcode = newHmiBarcode.Substring(0, newHmiBarcode.Length - 3);
        var gdtSeqno = newHmiBarcode.Substring(newHmiBarcode.Length - 3);

        // Lấy qty từ erp.dbo.gdtbart (Server34) để gán vào Sokgtem
        double sokgtem;
        using (var erp34Connection = new SqlConnection(_erpServer34ConnectionString))
        {
            await erp34Connection.OpenAsync();
            using var cmdQty = new SqlCommand(
                "SELECT TOP 1 [qty] FROM [erp].[dbo].[gdtbart] WHERE RTRIM([barcode]) = @barcode AND RTRIM([seqno]) = @seqno",
                erp34Connection);
            cmdQty.Parameters.AddWithValue("@barcode", gdtBarcode);
            cmdQty.Parameters.AddWithValue("@seqno", gdtSeqno);
            var qtyResult = await cmdQty.ExecuteScalarAsync();
            if (qtyResult == null || qtyResult == DBNull.Value)
            {
                TempData["ErrorMessage"] = $"Không tìm thấy qty trong gdtbart cho barcode='{gdtBarcode}', seqno='{gdtSeqno}'.";
                return RedirectToAction("Index", new { selectedBarcode });
            }
            sokgtem = Convert.ToDouble(qtyResult, CultureInfo.InvariantCulture);
        }

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Kiểm tra HMI Barcode đã tồn tại chưa
        using var cmdCheck = new SqlCommand(
            "SELECT COUNT(1) FROM [BB].[dbo].[bb_Oil_Nhaptay] WHERE [HMI_Barcode] = @hmiBarcode",
            connection);
        cmdCheck.Parameters.AddWithValue("@hmiBarcode", newHmiBarcode);
        var existsCount = (int)(await cmdCheck.ExecuteScalarAsync() ?? 0);
        if (existsCount > 0)
        {
            TempData["ErrorMessage"] = $"HMI Barcode '{newHmiBarcode}' đã tồn tại trong hệ thống. Không thể nhập trùng.";
            return RedirectToAction("Index", new { selectedBarcode });
        }

        // Indat / Intime = thời gian hiện tại
        var nowInsert = DateTime.Now;
        var newIndat = nowInsert.ToString("yyyyMMdd");
        var newIntime = nowInsert.ToString("HH:mm:ss");

        // Lấy Result_ActiveUp từ bản ghi gần nhất của cùng Barcode 7bit
        string? resultActiveUp = null;
        using (var cmdLatest = new SqlCommand(
            @"SELECT TOP 1 [Result_ActiveUp]
              FROM [BB].[dbo].[bb_Oil_Nhaptay]
              WHERE [Barcode_left_7bit] = @barcode
              ORDER BY [Indat] DESC, [Intime] DESC",
            connection))
        {
            cmdLatest.Parameters.AddWithValue("@barcode", selectedBarcode);
            var latestResult = await cmdLatest.ExecuteScalarAsync();
            if (latestResult != null && latestResult != DBNull.Value)
            {
                resultActiveUp = latestResult.ToString();
            }
        }

        // Insert new record: Sokgtem từ gdtbart.qty, sokgsudung = 0, active = 'mokhoa'
        using var cmdInsert = new SqlCommand(
            @"INSERT INTO [BB].[dbo].[bb_Oil_Nhaptay] ([Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit], [Sokgtem], [sokgsudung], [active])
              VALUES (@indat, @intime, @resultActiveUp, @hmiBarcode, @barcode, @sokgtem, @sokgsudung, @active);
              SELECT SCOPE_IDENTITY();",
            connection);
        cmdInsert.Parameters.AddWithValue("@indat", newIndat);
        cmdInsert.Parameters.AddWithValue("@intime", newIntime);
        cmdInsert.Parameters.AddWithValue("@resultActiveUp", (object?)resultActiveUp ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@hmiBarcode", newHmiBarcode);
        cmdInsert.Parameters.AddWithValue("@barcode", selectedBarcode);
        cmdInsert.Parameters.AddWithValue("@sokgtem", sokgtem);
        cmdInsert.Parameters.AddWithValue("@sokgsudung", 0d);
        cmdInsert.Parameters.AddWithValue("@active", "mokhoa");

        var newId = Convert.ToInt32(await cmdInsert.ExecuteScalarAsync());

        // Log the insert action
        await WriteLog(connection, "INSERT", newId, newIndat, newIntime, resultActiveUp, newHmiBarcode, selectedBarcode);

        TempData["SuccessMessage"] = $"Đã nhập mới thành công! ID: {newId}, HMI Barcode: {newHmiBarcode}, Ngày: {newIndat}, Giờ: {newIntime}, Result_ActiveUp: {resultActiveUp ?? "(trống)"}, Sokgtem: {sokgtem}";
        return RedirectToAction("Index", new { selectedBarcode });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? selectedBarcode)
    {
        if (id <= 0)
        {
            TempData["ErrorMessage"] = "ID không hợp lệ.";
            return RedirectToAction("Index", new { selectedBarcode });
        }

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Read the record before deleting for logging
        string? indat = null, intime = null, resultActiveUp = null, hmiBarcode = null, barcodeLeft7bit = null;
        using (var cmdRead = new SqlCommand(
            "SELECT [Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit] FROM [BB].[dbo].[bb_Oil_Nhaptay] WHERE [ID] = @id",
            connection))
        {
            cmdRead.Parameters.AddWithValue("@id", id);
            using var reader = await cmdRead.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                indat = reader.IsDBNull(0) ? null : reader.GetString(0);
                intime = reader.IsDBNull(1) ? null : reader.GetString(1);
                resultActiveUp = reader.IsDBNull(2) ? null : reader.GetString(2);
                hmiBarcode = reader.IsDBNull(3) ? null : reader.GetString(3);
                barcodeLeft7bit = reader.IsDBNull(4) ? null : reader.GetString(4);
            }
        }

        using var cmd = new SqlCommand(
            "DELETE FROM [BB].[dbo].[bb_Oil_Nhaptay] WHERE [ID] = @id",
            connection);
        cmd.Parameters.AddWithValue("@id", id);

        var rowsAffected = await cmd.ExecuteNonQueryAsync();

        if (rowsAffected > 0)
        {
            // Log the delete action
            await WriteLog(connection, "DELETE", id, indat, intime, resultActiveUp, hmiBarcode, barcodeLeft7bit);
            TempData["SuccessMessage"] = $"Đã xóa bản ghi ID = {id} thành công.";
        }
        else
        {
            TempData["ErrorMessage"] = $"Không tìm thấy bản ghi ID = {id} để xóa.";
        }

        return RedirectToAction("Index", new { selectedBarcode });
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private async Task LoadBarcodeList(OilViewModel viewModel, string? selectedBarcode)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = new SqlCommand(
            "SELECT DISTINCT [Barcode_left_7bit] FROM [BB].[dbo].[bb_Oil_Nhaptay] WHERE [Barcode_left_7bit] IS NOT NULL AND [Barcode_left_7bit] LIKE '68%' ORDER BY [Barcode_left_7bit]",
            connection);
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var value = reader.GetString(0).Trim();
            viewModel.BarcodeList.Add(new SelectListItem
            {
                Value = value,
                Text = value,
                Selected = value == selectedBarcode
            });
        }
    }

    private async Task LoadOilRecords(OilViewModel viewModel, string selectedBarcode)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = new SqlCommand(
            @"SELECT TOP 50 [ID], [Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit], [Sokgtem], [sokgsudung], [active]
              FROM [BB].[dbo].[bb_Oil_Nhaptay]
              WHERE [Barcode_left_7bit] = @barcode
              ORDER BY [Indat] DESC, [Intime] DESC",
            connection);
        cmd.Parameters.AddWithValue("@barcode", selectedBarcode);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            viewModel.OilRecords.Add(new BbOil
            {
                ID = reader.GetInt32(0),
                Indat = reader.IsDBNull(1) ? null : reader.GetString(1),
                Intime = reader.IsDBNull(2) ? null : reader.GetString(2),
                Result_ActiveUp = reader.IsDBNull(3) ? null : reader.GetString(3),
                HMI_Barcode = reader.IsDBNull(4) ? null : reader.GetString(4),
                Barcode_left_7bit = reader.IsDBNull(5) ? null : reader.GetString(5),
                Sokgtem = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                Sokgsudung = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                Active = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
    }

    private async Task EnsureLogTableExists(SqlConnection connection)
    {
        using var cmd = new SqlCommand(
            @"IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'bb_Oil_Log' AND schema_id = SCHEMA_ID('dbo'))
              BEGIN
                  CREATE TABLE [dbo].[bb_Oil_Log](
                      [LogID] [int] IDENTITY(1,1) NOT NULL,
                      [Action] [varchar](10) NOT NULL,
                      [RecordID] [int] NULL,
                      [Indat] [varchar](8) NULL,
                      [Intime] [varchar](8) NULL,
                      [Result_ActiveUp] [varchar](50) NULL,
                      [HMI_Barcode] [varchar](50) NULL,
                      [Barcode_left_7bit] [varchar](50) NULL,
                      [LogDate] [datetime] NOT NULL DEFAULT(GETDATE()),
                      [LogUser] [varchar](100) NULL
                  )
              END",
            connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WriteLog(SqlConnection connection, string action, int recordId,
        string? indat, string? intime, string? resultActiveUp, string? hmiBarcode, string? barcodeLeft7bit)
    {
        await EnsureLogTableExists(connection);

        using var cmd = new SqlCommand(
            @"INSERT INTO [BB].[dbo].[bb_Oil_Log] ([Action], [RecordID], [Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit], [LogUser])
              VALUES (@action, @recordId, @indat, @intime, @resultActiveUp, @hmiBarcode, @barcode, @logUser)",
            connection);
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@recordId", recordId);
        cmd.Parameters.AddWithValue("@indat", (object?)indat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@intime", (object?)intime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@resultActiveUp", (object?)resultActiveUp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hmiBarcode", (object?)hmiBarcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@barcode", (object?)barcodeLeft7bit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@logUser", Environment.UserName);

        await cmd.ExecuteNonQueryAsync();
    }
}
