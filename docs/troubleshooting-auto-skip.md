# 🔍 Troubleshooting: Auto-Skip ไม่ส่งข้อมูลไปชีท

## ปัญหาที่พบบ่อย:

### ❌ **ปัญหา 1: Google Apps Script ยังเป็นเวอร์ชันเก่า (10 คอลัมน์)**

**อาการ:**
- Console เห็น "✅ Successfully synced" แต่ชีทไม่มีข้อมูล
- หรือ Google Sheets มี error

**วิธีแก้:**
1. เปิด Google Sheets → Extensions → Apps Script
2. ลบโค้ดเก่าทั้งหมด
3. คัดลอกจาก `google-apps-script-FINAL.js`
4. Deploy → Test

**เช็คว่าถูกต้อง:**
```javascript
// ต้องเห็น numCols = 9 (ไม่ใช่ 10)
if (type === "action") {
  headers = getHeadersAction();
  numCols = 9;  // ✅ ต้องเป็น 9
}
```

---

### ❌ **ปัญหา 2: URL ของ Google Apps Script ผิด**

**อาการ:**
- Console ไม่มี error
- แต่ชีทไม่มีข้อมูล

**วิธีเช็ค:**
```cmd
# เปิด appsettings.json
# เช็คว่ามี URL นี้ไหม:
{
  "GoogleSheets": {
    "MonthlyCostAppScriptUrl": "https://script.google.com/macros/s/YOUR_SCRIPT_ID/exec"
  }
}
```

**วิธีแก้:**
1. Deploy Apps Script → คัดลอก URL
2. เปิด `appsettings.json`
3. อัปเดต URL

---

### ❌ **ปัญหา 3: AutoClosePriorMonthsAsync ไม่ทำงาน**

**อาการ:**
- กดปุ่ม "ข้าม 1 เดือน" แล้วไม่มี log ใน Console

**วิธีเช็ค:**
```javascript
// กด F12 → Console → พิมพ์:
console.log('Test');

// ถ้าไม่เห็น "Test" แปลว่า Console ไม่ทำงาน
```

**วิธีแก้:**
- Refresh หน้าเว็บ (F5)
- เปิด Console ใหม่ (F12)

---

### ❌ **ปัญหา 4: ไม่มีรายการที่ต้อง auto-skip**

**อาการ:**
- Console เห็น "No new auto-skip actions needed"

**วิธีเช็ค:**
```sql
-- เช็คว่ามีรายการที่ยังไม่มี action หรือไม่
SELECT 
    otm.Id,
    otm.PoNumber,
    otm.Remarks,
    otm.ApprovedDate,
    moa.MonthYear,
    moa.Action
FROM OrderTrackingMasters otm
LEFT JOIN MonthlyOrderActions moa 
    ON otm.Id = moa.OrderTrackingMasterId
WHERE otm.ApprovedDate IS NOT NULL
  AND (moa.MonthYear IS NULL OR moa.MonthYear = '2026-01')
ORDER BY otm.ApprovedDate DESC;
```

**วิธีแก้:**
- ถ้าไม่มีรายการเลย → สร้างรายการใหม่ในเดือนมกราคม
- ถ้ามีแล้ว → เลือกบางรายการแล้วบันทึก

---

## ✅ **วิธีเช็คว่าระบบทำงานถูกต้อง:**

### **1. เช็ค Console Log:**
```
[AutoClosePriorMonthsAsync] Auto-skipped items in months: 2026-01
[AutoClosePriorMonthsAsync] ✅ Successfully synced 1 month(s) to Google Sheets
```

### **2. เช็ค DB:**
```sql
-- ต้องเห็นรายการ auto-skip (Action = 'Skipped')
SELECT MonthYear, Action, COUNT(*) as Count
FROM MonthlyOrderActions
WHERE MonthYear = '2026-01'
GROUP BY Action;

-- ผลลัพธ์ที่ถูกต้อง:
-- MonthYear | Action        | Count
-- 2026-01   | ReceivedFull  | 6
-- 2026-01   | Skipped       | 4
```

### **3. เช็ค Google Sheets:**
- ต้องเห็น **เดือน: มกราคม 2569**
- มี **10 แถว** (6 เดิม + 4 auto-skip)
- แถวที่ auto-skip มีสถานะ **"ยังไม่รับสินค้า"**

---

## 🔧 **Quick Fix:**

ถ้ายังไม่ทำงาน ลองรัน manual sync:

### **วิธี 1: เรียก API โดยตรง**
```javascript
// เปิด Console (F12) → พิมพ์:
fetch('http://localhost:5125/MonthlyCost/TriggerAutoSkip', {
  method: 'POST'
})
.then(r => r.json())
.then(data => console.log(data));
```

### **วิธี 2: รันใน C#**
```sql
-- เพิ่ม endpoint ใน MonthlyCostController.cs:
[HttpPost]
public async Task<IActionResult> TriggerAutoSkip()
{
    await AutoClosePriorMonthsAsync();
    return Ok(new { success = true });
}
```

แล้วเรียกจาก browser:
```
http://localhost:5125/MonthlyCost/TriggerAutoSkip
```

---

## 📝 **Checklist:**

- [ ] ✅ SQL แก้ไข "2025-122" → "2025-12"
- [ ] ✅ Google Apps Script อัปเดตเป็น 9 คอลัมน์
- [ ] ✅ Build และรัน app ใหม่
- [ ] ✅ เลือกบางรายการในเดือนมกราคม
- [ ] ✅ กดปุ่ม "ข้าม 1 เดือน ▶"
- [ ] ✅ ดู Console log
- [ ] ✅ ดู Google Sheets

---

## 💡 **สรุป:**

ถ้าทำตาม checklist แล้ว **ยังไม่ทำงาน** → ส่ง screenshot ของ:
1. Console log (F12)
2. Google Sheets
3. ผลลัพธ์จาก SQL query

แล้วเราจะช่วยแก้ไขต่อ! 🚀
