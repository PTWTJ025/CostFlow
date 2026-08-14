# 🚀 Setup Background Service สำหรับ Auto-Skip อัตโนมัติ

## 📋 สิ่งที่ได้:
- ระบบ auto-skip รันทุกวันเวลา **00:00** อัตโนมัติ
- ไม่ต้องติดตั้งอะไรเพิ่ม
- รองรับ **Dev Time Travel**
- มี Logging ครบถ้วน

---

## ⚡ ขั้นตอนการติดตั้ง (3 ขั้นตอน):

### 1️⃣ แก้ไขปัญหา "2025-122" ใน DB ก่อน (สำคัญ!)

เปิด **DBeaver** → รัน SQL นี้:

```sql
-- แก้ไข "2025-122" → "2025-12"
UPDATE MonthlyOrderActions
SET MonthYear = '2025-12'
WHERE MonthYear = '2025-122';

-- แก้ไขทั่วไป
UPDATE MonthlyOrderActions
SET MonthYear = SUBSTR(MonthYear, 1, 7)
WHERE LENGTH(MonthYear) > 7;

-- ตรวจสอบ (ต้องไม่มี "2025-122" แล้ว)
SELECT DISTINCT MonthYear 
FROM MonthlyOrderActions
ORDER BY MonthYear DESC;
```

---

### 2️⃣ Register Service ใน `Program.cs`

เปิดไฟล์ `Program.cs` → เพิ่มบรรทัดนี้:

```csharp
// หาบรรทัดที่มี:
builder.Services.AddControllersWithViews();

// เพิ่มบรรทัดนี้ลงไปด้านล่าง:
builder.Services.AddHostedService<AutoSkipBackgroundService>();
```

**ตัวอย่างเต็ม:**
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
builder.Services.AddHttpClient("GoogleAppsScript");

// ⭐ เพิ่มบรรทัดนี้
builder.Services.AddHostedService<AutoSkipBackgroundService>();

var app = builder.Build();
// ...
```

---

### 3️⃣ Build และรัน

```cmd
cd d:\ProjectIntern\CostFlow
dotnet build
dotnet run
```

---

## ✅ ตรวจสอบว่าทำงาน:

เมื่อรันแล้ว ดู Console จะเห็น log แบบนี้:

```
🚀 Auto-Skip Background Service เริ่มทำงาน
⏰ Auto-Skip ครั้งถัดไป: 14/01/2026 00:00:00 (อีก 3.5 ชั่วโมง)
```

พอถึง 00:00 จะเห็น:

```
🔄 กำลังรัน Auto-Skip... (เวลา: 14/01/2026 00:00:00)
💾 บันทึก 3 รายการใน 1 เดือน: 2025-01
📤 กำลังส่งข้อมูลไป Google Sheets...
✅ ส่งข้อมูลไป Google Sheets สำเร็จ
✅ Auto-Skip เสร็จสมบูรณ์
⏰ Auto-Skip ครั้งถัดไป: 15/01/2026 00:00:00 (อีก 24.0 ชั่วโมง)
```

---

## 🧪 ทดสอบด้วย Dev Time Travel:

1. กดปุ่ม "Dev Time Travel"
2. ตั้งเวลาเป็น `13/01/2026 23:59:00`
3. รอ 1 นาที → ระบบจะรัน auto-skip อัตโนมัติ

---

## ⚠️ ข้อควรระวัง:

1. **App ต้องรันอยู่ตลอดเวลา** - ถ้าปิด app ระบบจะไม่ทำงาน
2. **ไม่รองรับ Dev Time Travel กับ Windows Task Scheduler**
3. **Production**: ถ้า deploy บน IIS/Azure → ต้องตั้งค่า "Always On"

---

## 📊 การทำงาน:

```
23:59:59 → รอ 1 วินาที...
00:00:00 → 🚀 เริ่ม Auto-Skip
00:00:01 → เช็ค orders ที่ยังไม่มี action
00:00:02 → สร้าง Skipped records
00:00:03 → บันทึกลง DB
00:00:04 → ส่งไป Google Sheets
00:00:05 → ✅ เสร็จสิ้น
00:00:06 → รอจนถึง 00:00 วันถัดไป...
```

---

## 🔧 Troubleshooting:

### ❓ ไม่เห็น log "Auto-Skip Background Service"
→ ลืม register ใน `Program.cs`

### ❓ ยังเห็น "2025-122" ในชีท
→ ยังไม่ได้รัน SQL แก้ไขใน DB

### ❓ Auto-Skip ไม่ทำงานเวลา 00:00
→ เช็คว่า app ยังรันอยู่หรือไม่

---

## 🎯 สรุป:

✅ ง่าย - แค่ 3 ขั้นตอน  
✅ ไม่ต้องติดตั้งอะไรเพิ่ม  
✅ รองรับ Dev Time Travel  
✅ มี Logging ครบถ้วน  

**ข้อเสีย**เดียว: App ต้องรันตลอดเวลา 🏃
