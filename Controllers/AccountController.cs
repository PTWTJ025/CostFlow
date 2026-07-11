using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;
using CostFlow.Models;

namespace CostFlow.Controllers
{
    [AllowAnonymous]
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager)
        {
            _signInManager = signInManager;
            _userManager = userManager;
        }

        // GET: /Account/Login
        [HttpGet]
        public async Task<IActionResult> Login()
        {
            // ถ้า login อยู่แล้ว แยกไปตามบทบาท
            if (User.Identity?.IsAuthenticated == true)
            {
                if (User.IsInRole("Admin"))
                {
                    return RedirectToAction("Index", "Home");
                }
                return RedirectToAction("Index", "ProductSearch");
            }

            var admin = await _userManager.FindByNameAsync("ADMIN01");
            ViewBag.AdminName = admin?.FullName ?? "แมวกวนๆ";
            ViewBag.AdminPhoto = admin?.ProfilePictureUrl
                ?? "https://cdn.readawrite.com/articles/11729/11728659/thumbnail/large.gif?1";

            return View();
        }

        // POST: /Account/SubmitLogin
        [HttpPost]
        public async Task<IActionResult> SubmitLogin(string email, string password, bool rememberMe = false)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                return Json(new { success = false, error = "กรุณากรอกข้อมูลให้ครบถ้วน" });
            }

            string employeeCode = email.Trim().ToUpper();

            // ค้นหา user จาก EmployeeCode (ใช้ UserName = EmployeeCode)
            var user = await _userManager.FindByNameAsync(employeeCode);

            if (user == null)
            {
                return Json(new { success = false, error = "ไม่พบรหัสพนักงานนี้ในระบบ" });
            }

            if (!user.IsActive)
            {
                return Json(new { success = false, error = "บัญชีนี้ถูกระงับการใช้งานชั่วคราว" });
            }

            // ตรวจสอบรหัสผ่าน + set cookie อัตโนมัติ (ใช้ persistent cookie เมื่อ rememberMe เป็นจริง)
            var result = await _signInManager.PasswordSignInAsync(
                user,
                password,
                isPersistent: rememberMe,  // Persistent cookie — ปิดเบราว์เซอร์แล้วยังอยู่ตามที่ผู้ใช้เลือก
                lockoutOnFailure: false
            );

            if (result.Succeeded)
            {
                bool isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
                string redirect = isAdmin ? Url.Action("Index", "Home")! : Url.Action("Index", "ProductSearch")!;
                return Json(new { success = true, redirectUrl = redirect });
            }

            return Json(new { success = false, error = "รหัสผ่านไม่ถูกต้อง" });
        }

        // GET: /Account/Logout
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }
    }
}
