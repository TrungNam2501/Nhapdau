using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using NhapdauWeb.Models;

namespace NhapdauWeb.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly string _connectionString;

    public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("Server33")
            ?? throw new InvalidOperationException("Connection string 'Server33' not found.");
    }

    public async Task<IActionResult> Index(string? selectedBarcode)
    {
        var viewModel = new OilViewModel
        {
            SelectedBarcode = selectedBarcode
        };

        // Load distinct Barcode_left_7bit for dropdown
        using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();

            using var cmdDistinct = new SqlCommand(
                "SELECT DISTINCT [Barcode_left_7bit] FROM [BB].[dbo].[bb_Oil] WHERE [Barcode_left_7bit] IS NOT NULL ORDER BY [Barcode_left_7bit]",
                connection);
            using var reader = await cmdDistinct.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var value = reader.GetString(0);
                viewModel.BarcodeList.Add(new SelectListItem
                {
                    Value = value,
                    Text = value,
                    Selected = value == selectedBarcode
                });
            }
        }

        // If a barcode is selected, load filtered records
        if (!string.IsNullOrEmpty(selectedBarcode))
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new SqlCommand(
                @"SELECT [ID], [Indat], [Intime], [Result_ActiveUp], [HMI_Barcode], [Barcode_left_7bit]
                  FROM [BB].[dbo].[bb_Oil]
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
                    Barcode_left_7bit = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        return View(viewModel);
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
}
