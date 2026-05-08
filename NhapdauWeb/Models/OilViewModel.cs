using Microsoft.AspNetCore.Mvc.Rendering;

namespace NhapdauWeb.Models;

public class OilViewModel
{
    public string? SelectedBarcode { get; set; }
    public List<SelectListItem> BarcodeList { get; set; } = new();
    public List<BbOil> OilRecords { get; set; } = new();
    public string? NewHmiBarcode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }
    public double TotalSokgtemMonth { get; set; }
    public double TotalSokgsudungMonth { get; set; }
}
