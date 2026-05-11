using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
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
        if (HttpContext.Session.GetString("Username") == null)
        {
            return RedirectToAction("Login", "Account");
        }

        // Chuẩn hoá: trim và chỉ lấy phần code (trước dấu cách) để chắc chắn
        // dùng đúng "68010" cho cột [Barcode_left_7bit] kể cả khi UI/URL truyền
        // chuỗi hiển thị kiểu "68010 - P150A".
        var normalizedBarcode = NormalizeBarcode(selectedBarcode);

        var viewModel = new OilViewModel
        {
            SelectedBarcode = normalizedBarcode,
            ErrorMessage = TempData["ErrorMessage"] as string,
            SuccessMessage = TempData["SuccessMessage"] as string
        };

        LoadBarcodeList(viewModel, normalizedBarcode);

        if (!string.IsNullOrEmpty(normalizedBarcode))
        {
            await LoadOilRecords(viewModel, normalizedBarcode);
            await LoadMonthlyTotals(viewModel, normalizedBarcode);
        }

        return View(viewModel);
    }

    private static string? NormalizeBarcode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace > 0)
        {
            trimmed = trimmed.Substring(0, firstSpace);
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Insert(string selectedBarcode, string newHmiBarcode)
    {
        if (HttpContext.Session.GetString("Username") == null)
        {
            return RedirectToAction("Login", "Account");
        }

        // Chuẩn hoá tương tự Index để chắc chắn lưu đúng code 5 ký tự vào DB
        selectedBarcode = NormalizeBarcode(selectedBarcode) ?? string.Empty;

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

        // Tách HMI Barcode: 3 ký tự cuối = seqno, phần còn lại = barcode
        if (newHmiBarcode.Length < 4)
        {
            TempData["ErrorMessage"] = $"HMI Barcode '{newHmiBarcode}' không hợp lệ để tách barcode/seqno (độ dài tối thiểu 4 ký tự).";
            return RedirectToAction("Index", new { selectedBarcode });
        }
        var gdtBarcode = newHmiBarcode.Substring(0, newHmiBarcode.Length - 3);
        var gdtSeqno = newHmiBarcode.Substring(newHmiBarcode.Length - 3);

        // Kiểm tra tem có tồn tại trong erp.dbo.gdtbart chưa (Server34) - PHẢI làm trước khi check nghiệm thu.
        // Đồng thời lấy luôn qty để gán vào Sokgtem sau này (chỉ cần 1 query thay vì 2).
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
                TempData["ErrorMessage"] = $"Tem nhập sai hoặc tem vừa in, vui lòng kiểm tra lại mã vạch '{newHmiBarcode}'. Nếu tem đúng, chờ 10 phút sau nhập lại.";
                return RedirectToAction("Index", new { selectedBarcode });
            }
            sokgtem = Convert.ToDouble(qtyResult, CultureInfo.InvariantCulture);
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
                TempData["ErrorMessage"] = $"HMI Barcode '{newHmiBarcode}' chưa nghiệm thu. Vui lòng liên hệ thí nghiệm dùng chương trình nghiệm thu trên máy quét nghiệm thu rồi nhập lại";
                return RedirectToAction("Index", new { selectedBarcode });
            }
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
        var currentUser = HttpContext.Session.GetString("Username") ?? "";
        using var cmdInsert = new SqlCommand(
            @"INSERT INTO [BB].[dbo].[bb_Oil_Nhaptay] ([Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit], [Sokgtem], [sokgsudung], [active], [User])
              VALUES (@indat, @intime, @resultActiveUp, @hmiBarcode, @barcode, @sokgtem, @sokgsudung, @active, @user);
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
        cmdInsert.Parameters.AddWithValue("@user", currentUser);

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
        if (HttpContext.Session.GetString("Username") == null)
        {
            return RedirectToAction("Login", "Account");
        }

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

    public async Task<IActionResult> ExportExcel(string? selectedBarcode)
    {
        if (HttpContext.Session.GetString("Username") == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var normalizedBarcode = NormalizeBarcode(selectedBarcode);
        if (string.IsNullOrEmpty(normalizedBarcode) || !OilTypes.IsValidCode(normalizedBarcode))
        {
            TempData["ErrorMessage"] = "Vui lòng chọn loại dầu hợp lệ trước khi tải Excel.";
            return RedirectToAction("Index", new { selectedBarcode });
        }

        // Tải TOÀN BỘ records của loại dầu này (không TOP 50 như bảng hiển thị)
        var records = new List<BbOil>();
        using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            using var cmd = new SqlCommand(
                @"SELECT [ID], [Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit], [Sokgtem], [sokgsudung], [active], [User]
                  FROM [BB].[dbo].[bb_Oil_Nhaptay]
                  WHERE [Barcode_left_7bit] = @barcode
                  ORDER BY [Indat] DESC, [Intime] DESC",
                connection);
            cmd.Parameters.AddWithValue("@barcode", normalizedBarcode);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new BbOil
                {
                    ID = reader.GetInt32(0),
                    Indat = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Intime = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Result_ActiveUp = reader.IsDBNull(3) ? null : reader.GetString(3),
                    HMI_Barcode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Barcode_left_7bit = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Sokgtem = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    Sokgsudung = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    Active = reader.IsDBNull(8) ? null : reader.GetString(8),
                    User = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }
        }

        using var workbook = new XLWorkbook();
        var displayName = OilTypes.GetDisplayName(normalizedBarcode);
        // Excel sheet name: max 31 chars, không chứa \ / ? * [ ]
        var sheetName = Regex.Replace(displayName, @"[\\/?*\[\]]", "_");
        if (sheetName.Length > 31) sheetName = sheetName.Substring(0, 31);
        var ws = workbook.Worksheets.Add(sheetName);

        var headers = new[]
        {
            "ID", "Ngày nhập", "Thời gian", "Result ActiveUp",
            "HMI Barcode", "Barcode 7bit", "Số kg tem", "Số kg sử dụng",
            "Active", "User"
        };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }
        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#212529");
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        int row = 2;
        foreach (var rec in records)
        {
            // Cap sokgsudung tối đa bằng Sokgtem cho từng dòng (giống hiển thị bảng)
            double? displaySokgsudung = rec.Sokgsudung;
            if (displaySokgsudung.HasValue && rec.Sokgtem.HasValue && displaySokgsudung.Value > rec.Sokgtem.Value)
            {
                displaySokgsudung = rec.Sokgtem;
            }

            ws.Cell(row, 1).Value = rec.ID;
            ws.Cell(row, 2).Value = rec.Indat ?? string.Empty;
            ws.Cell(row, 3).Value = rec.Intime ?? string.Empty;
            ws.Cell(row, 4).Value = rec.Result_ActiveUp ?? string.Empty;
            ws.Cell(row, 5).Value = rec.HMI_Barcode ?? string.Empty;
            ws.Cell(row, 6).Value = rec.Barcode_left_7bit ?? string.Empty;
            if (rec.Sokgtem.HasValue) ws.Cell(row, 7).Value = rec.Sokgtem.Value;
            if (displaySokgsudung.HasValue) ws.Cell(row, 8).Value = displaySokgsudung.Value;
            ws.Cell(row, 9).Value = rec.Active ?? string.Empty;
            ws.Cell(row, 10).Value = rec.User ?? string.Empty;
            row++;
        }

        ws.Column(7).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(8).Style.NumberFormat.Format = "#,##0.00";
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var fileName = $"BB_Oil_{normalizedBarcode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static void LoadBarcodeList(OilViewModel viewModel, string? selectedBarcode)
    {
        // Danh sách loại dầu được gán cứng (xem Models/OilType.cs)
        foreach (var oil in OilTypes.All)
        {
            viewModel.BarcodeList.Add(new SelectListItem
            {
                Value = oil.Code,
                Text = oil.DisplayName,
                Selected = string.Equals(oil.Code, selectedBarcode, StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    private async Task LoadOilRecords(OilViewModel viewModel, string selectedBarcode)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = new SqlCommand(
            @"SELECT TOP 50 [ID], [Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit], [Sokgtem], [sokgsudung], [active], [User]
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
                Active = reader.IsDBNull(8) ? null : reader.GetString(8),
                User = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
    }

    private async Task LoadMonthlyTotals(OilViewModel viewModel, string selectedBarcode)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await EnsureSudungLogTableExists(connection);

        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1).ToString("yyyyMMdd");
        var nextMonthStart = new DateTime(now.Year, now.Month, 1).AddMonths(1).ToString("yyyyMMdd");

        // Tổng kg tem (tháng): SUM Sokgtem từ bb_Oil_Nhaptay lọc theo Indat (ngày tem được nhập).
        // Tổng kg sử dụng (tháng): SUM RealWeight từ bb_Oil_Sudung_Log lọc theo Sudungdat (ngày dầu thực sự
        // được sử dụng — OILautoservice.UpdateSokgsudungAsync ghi delta vào bảng log mỗi lần update).
        // Cách này chia chính xác giữa các tháng kể cả khi 1 tem được dùng dần qua nhiều tháng.
        using var cmd = new SqlCommand(
            @"SELECT
                ISNULL((
                    SELECT SUM([Sokgtem])
                    FROM [BB].[dbo].[bb_Oil_Nhaptay]
                    WHERE [Barcode_left_7bit] = @barcode
                      AND [Indat] >= @monthStart
                      AND [Indat] < @nextMonthStart
                ), 0) AS TotalSokgtem,
                ISNULL((
                    SELECT SUM([RealWeight])
                    FROM [BB].[dbo].[bb_Oil_Sudung_Log]
                    WHERE [Barcode_left_7bit] = @barcode
                      AND [Sudungdat] >= @monthStart
                      AND [Sudungdat] < @nextMonthStart
                ), 0) AS TotalSokgsudung",
            connection);
        cmd.Parameters.AddWithValue("@barcode", selectedBarcode);
        cmd.Parameters.AddWithValue("@monthStart", monthStart);
        cmd.Parameters.AddWithValue("@nextMonthStart", nextMonthStart);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            viewModel.TotalSokgtemMonth = reader.IsDBNull(0) ? 0d : Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture);
            viewModel.TotalSokgsudungMonth = reader.IsDBNull(1) ? 0d : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
    }

    private static async Task EnsureSudungLogTableExists(SqlConnection connection)
    {
        // Idempotent: tạo bảng log delta cho sokgsudung nếu chưa có.
        // OILautoservice.UpdateSokgsudungAsync sẽ INSERT 1 dòng vào đây mỗi lần cộng dồn realWeight.
        using var cmd = new SqlCommand(
            @"IF NOT EXISTS (SELECT 1 FROM sys.tables
                             WHERE name = 'bb_Oil_Sudung_Log'
                               AND schema_id = SCHEMA_ID('dbo'))
              BEGIN
                  CREATE TABLE [BB].[dbo].[bb_Oil_Sudung_Log](
                      [LogID]             [int]            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                      [NhaptayID]         [int]            NOT NULL,
                      [Barcode_left_7bit] [varchar](50)    NULL,
                      [RealWeight]        [decimal](18,6)  NOT NULL,
                      [Sudungdat]         [varchar](8)     NULL,
                      [Sudungtime]        [varchar](8)     NULL,
                      [LogDate]           [datetime]       NOT NULL DEFAULT(GETDATE())
                  );
              END;
              IF NOT EXISTS (SELECT 1 FROM sys.indexes
                             WHERE name = 'IX_bb_Oil_Sudung_Log_Barcode_Sudungdat'
                               AND object_id = OBJECT_ID('[BB].[dbo].[bb_Oil_Sudung_Log]'))
              BEGIN
                  CREATE INDEX [IX_bb_Oil_Sudung_Log_Barcode_Sudungdat]
                  ON [BB].[dbo].[bb_Oil_Sudung_Log] ([Barcode_left_7bit], [Sudungdat]);
              END;",
            connection);
        await cmd.ExecuteNonQueryAsync();
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

        var currentUser = HttpContext.Session.GetString("Username") ?? "";
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
        cmd.Parameters.AddWithValue("@logUser", currentUser);

        await cmd.ExecuteNonQueryAsync();
    }
}
