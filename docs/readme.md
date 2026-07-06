CostFlow/
├── Controllers/          ← MVC สร้างให้อัตโนมัติ (HomeController.cs มาให้แล้ว)
├── Models/                ← MVC สร้างให้อัตโนมัติ
├── Views/
│   ├── Home/               (มาให้แล้ว — ใช้ทำหน้า Dashboard)
│   ├── FileImport/          ← โมดูล A
│   ├── FileMerge/           ← โมดูล A
│   ├── Report/              ← โมดูล A
│   ├── ProductSearch/       ← โมดูล B
│   ├── PriceReference/      ← โมดูล B
│   └── Shared/              ← _Layout.cshtml (Sidebar อยู่ตรงนี้)
├── Services/               ← ตรรกะอ่านไฟล์, merge, ค้นหาสินค้า
├── Data/                   ← AppDbContext.cs
├── ViewModels/             ← โมเดลเฉพาะหน้าจอ
├── wwwroot/
│   ├── css/custom/
│   └── js/custom/
└── Program.cs