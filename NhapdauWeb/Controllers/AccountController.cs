using System.Net;
using Microsoft.AspNetCore.Mvc;
using NhapdauWeb.Models;

namespace NhapdauWeb.Controllers;

public class AccountController : Controller
{
    private readonly IConfiguration _configuration;

    public AccountController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public IActionResult Login()
    {
        if (HttpContext.Session.GetString("Username") != null)
        {
            return RedirectToAction("Index", "Home");
        }
        return View(new LoginViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var ftpServer = _configuration.GetValue<string>("FtpSettings:Server") ?? "ftp://192.1.1.1/";

        if (FtpLogin(ftpServer, model.Username, model.Password))
        {
            HttpContext.Session.SetString("Username", model.Username);
            return RedirectToAction("Index", "Home");
        }

        model.ErrorMessage = "Sai tai khoan hoac mat khau. Vui long thu lai.";
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Login");
    }

    private bool FtpLogin(string server, string id, string pw)
    {
#pragma warning disable SYSLIB0014
        try
        {
            var fwr = (FtpWebRequest)WebRequest.Create(server);
            fwr.Method = WebRequestMethods.Ftp.ListDirectory;
            fwr.Credentials = new NetworkCredential(id, pw);
            fwr.Timeout = 5000;

            using var fwre = (FtpWebResponse)fwr.GetResponse();
            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore SYSLIB0014
    }
}
