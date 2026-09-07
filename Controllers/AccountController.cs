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
                var user = await _userManager.GetUserAsync(User);
                string userName = User.Identity?.Name ?? user?.UserName ?? "";
                bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Dev") ||
                               userName.Equals("ADMIN01", StringComparison.OrdinalIgnoreCase) ||
                               userName.Equals("DEV01", StringComparison.OrdinalIgnoreCase);

                if (isAdmin)
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

            // ตรวจสอบรหัสผ่าน + set cookie เป็น Session Cookie (isPersistent = false)
            // ตามมาตรฐานความปลอดภัย Production: เมื่อปิดเบราว์เซอร์แล้วเปิดใหม่ จะต้องล็อกอินใหม่เสมอ
            // และระหว่างเปิดใช้งาน จะอยู่ได้ 8 ชม. ต่อเวลาอัตโนมัติ และไม่ออกเมื่อรีสตาร์ตเซิร์ฟเวอร์
            var result = await _signInManager.PasswordSignInAsync(
                user,
                password,
                isPersistent: false,
                lockoutOnFailure: false
            );

            if (result.Succeeded)
            {
                // ตรวจสอบและผูก Role อัตโนมัติหากยังไม่มี (Self-healing role assignment)
                if (user.UserName == "ADMIN01" && !await _userManager.IsInRoleAsync(user, "Admin"))
                {
                    await _userManager.AddToRoleAsync(user, "Admin");
                    await _signInManager.RefreshSignInAsync(user);
                }
                else if (user.UserName == "DEV01" && !await _userManager.IsInRoleAsync(user, "Dev"))
                {
                    await _userManager.AddToRoleAsync(user, "Dev");
                    await _signInManager.RefreshSignInAsync(user);
                }
                else if (user.UserName == "STAFF01" && !await _userManager.IsInRoleAsync(user, "Staff"))
                {
                    await _userManager.AddToRoleAsync(user, "Staff");
                    await _signInManager.RefreshSignInAsync(user);
                }

                bool isAdmin = user.UserName == "ADMIN01" || user.UserName == "DEV01" ||
                               await _userManager.IsInRoleAsync(user, "Admin") || 
                               await _userManager.IsInRoleAsync(user, "Dev");

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
