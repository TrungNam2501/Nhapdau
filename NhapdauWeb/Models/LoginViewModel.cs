using System.ComponentModel.DataAnnotations;

namespace NhapdauWeb.Models;

public class LoginViewModel
{
    [Required(ErrorMessage = "Vui long nhap tai khoan.")]
    [Display(Name = "Tai khoan")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui long nhap mat khau.")]
    [DataType(DataType.Password)]
    [Display(Name = "Mat khau")]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
}
