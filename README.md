# CostFlow — ระบบตรวจสอบข้อมูลจัดซื้ออะไหล่

ระบบเว็บภายในสำหรับตรวจสอบ เปรียบเทียบ และบันทึกข้อมูลจัดซื้ออะไหล่ของโรงงาน  
พัฒนาด้วย **ASP.NET Core 9 (C# MVC)** + **MySQL** + **Tailwind CSS**

---

## เริ่มต้นใช้งาน (Local Setup)

### สิ่งที่ต้องติดตั้งก่อน
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- MySQL Server (หรือเชื่อมต่อ Cloud ได้เลย)

### ขั้นตอน
```bash
# 1. Clone โปรเจกต์
git clone <repo-url>
cd CostFlow

# 2. สร้างไฟล์ตั้งค่าจาก Template
copy appsettings.example.json appsettings.json
# แก้ไข appsettings.json ให้ใส่ข้อมูลเชื่อมต่อฐานข้อมูลของคุณ

# 3. รัน
dotnet run
```

เปิดเบราว์เซอร์ไปที่ `https://localhost:5001`

> ตอนรันครั้งแรก ระบบจะสร้างตารางและข้อมูลเริ่มต้นให้อัตโนมัติ

---

## บัญชีเริ่มต้น (Default Accounts)

| รหัสพนักงาน | รหัสผ่าน | สิทธิ์ |
|---|---|---|
| `ADMIN01` | `admin1234` | Admin |
| `STAFF01` | `123456` | Staff |

---

## โครงสร้างโปรเจกต์

```
CostFlow/
├── Controllers/        ตัวควบคุมแต่ละฟีเจอร์
├── Models/             โมเดลข้อมูล (Entity)
├── Views/              หน้าเว็บ (Razor .cshtml)
├── Services/           คลาสช่วยงานด้านการนำเข้าไฟล์
├── Data/               AppDbContext (ตัวเชื่อมต่อฐานข้อมูล)
├── wwwroot/            ไฟล์ Static (CSS, JS, รูปภาพ, JSON)
├── Program.cs          จุดเริ่มต้นการทำงานของระบบ
└── appsettings.json    ไฟล์ตั้งค่า (ไม่ถูก Push ขึ้น Git)
```

---

## เอกสารแยกตามฟีเจอร์

| ฟีเจอร์ | ไฟล์เอกสาร |
|---|---|
| รวมและเปรียบเทียบไฟล์ Excel | [docs/01_file_merge.md](docs/01_file_merge.md) |
| Export รายงานเป็น Excel | [docs/02_export_excel.md](docs/02_export_excel.md) |
| คีย์ข้อมูลใบสั่งซื้ออะไหล่ | [docs/03_purchase_order.md](docs/03_purchase_order.md) |
| จัดการราคากลางอ้างอิง | [docs/04_price_reference.md](docs/04_price_reference.md) |
| รายงานและประวัติการรวมไฟล์ | [docs/05_reports.md](docs/05_reports.md) |

---

## ฐานข้อมูล

ใช้ **MySQL** เชื่อมต่อผ่าน Entity Framework Core (Pomelo)  
ตารางหลักที่เราดูแล:

| ตาราง | เก็บข้อมูลอะไร |
|---|---|
| `ProductPrices` | ราคากลางอะไหล่ทุกรายการ |
| `SparePartOrderBatches` | หัวใบสั่งซื้อ (กลุ่มรายการ) |
| `SparePartOrders` | รายการสั่งซื้อแต่ละชิ้น |
| `ImportSessions` | ประวัติการรวมไฟล์แต่ละครั้ง |
| `MergeResults` | ผลลัพธ์การเปรียบเทียบราคา |

ตารางที่ขึ้นต้นด้วย `AspNet...` เป็นของระบบล็อกอิน Microsoft ไม่ต้องแก้ไขครับ

---

*จัดทำโดย: ฝ่าย IT / พนักงานฝึกงาน*
