using Microsoft.AspNetCore.Mvc.Rendering;

namespace NhapdauWeb.Models;

public class OilViewModel
{
    public string? SelectedBarcode { get; set; }
    public List<SelectListItem> BarcodeList { get; set; } = new();
    public List<BbOil> OilRecords { get; set; } = new();
}
