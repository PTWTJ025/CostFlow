# ฟีเจอร์ที่ 4 — จัดการราคากลางอ้างอิง (เพิ่ม / แก้ไข / ลบ)

## ทำอะไร
แสดงฐานข้อมูลราคากลางอะไหล่ทั้งหมด และให้ Admin จัดการข้อมูลได้ (เพิ่ม แก้ไข ลบ) ส่วน Staff ดูได้อย่างเดียว

## ไฟล์ที่เกี่ยวข้อง

| ไฟล์ | หน้าที่ |
|---|---|
| `Controllers/PriceReferenceController.cs` | API เพิ่ม / แก้ไข / ลบ ราคากลาง |
| `Views/PriceReference/Index.cshtml` | หน้าแสดงตาราง ค้นหา และ Popup จัดการ |
| `Models/ProductPrice.cs` | โมเดลข้อมูลราคากลาง |

---

## ขั้นตอนการทำงาน (Flow)

```
ผู้ใช้เปิดหน้า "ราคากลางอ้างอิง"
        ↓
PriceReferenceController.Index() โหลดข้อมูลจาก ProductPrices
แสดงตาราง 50 รายการต่อหน้า พร้อมช่องค้นหา
        ↓
--- ค้นหา (ทุกคนทำได้) ---
ผู้ใช้พิมพ์ในช่องค้นหา
JavaScript ส่งคำขอ AJAX ไปยัง /PriceReference/SearchApi
แสดงผลในตารางโดยไม่รีโหลดหน้า

--- เพิ่ม / แก้ไข (Admin เท่านั้น) ---
Admin กดปุ่ม "เพิ่ม" หรือ "แก้ไข"
Popup Modal เปิดขึ้นมาพร้อมฟอร์ม
Admin กรอกข้อมูลแล้วกด "บันทึก"
JavaScript ส่ง POST ไปยัง /PriceReference/Create หรือ /Edit
Controller บันทึกลงตาราง ProductPrices
ตารางหน้าจอรีเฟรชใหม่อัตโนมัติ

--- ลบ (Admin เท่านั้น) ---
Admin กดปุ่ม "ลบ"
Popup ยืนยันการลบปรากฏขึ้น
Admin กด "ยืนยัน"
JavaScript ส่ง POST ไปยัง /PriceReference/Delete
Controller ลบออกจากตาราง ProductPrices
```

---

## ฟังก์ชันหลักใน Controller

### `Index(string search, int page)` — GET /PriceReference
- โหลดข้อมูลจากตาราง `ProductPrices` แบบแบ่งหน้า (50 รายการ/หน้า)
- ถ้ามี search จะกรองตามรหัสสินค้าหรือชื่อสินค้า

### `SearchApi(string search, int page)` — GET /PriceReference/SearchApi
- เหมือน Index แต่คืนค่าเป็น JSON สำหรับ AJAX
- ใช้โดย JavaScript ตอนผู้ใช้พิมพ์ค้นหา

### `Create([FromBody] ProductPrice model)` — POST (Admin only)
- ตรวจสอบว่ารหัสสินค้าซ้ำหรือไม่ก่อนบันทึก
- บันทึกลงตาราง `ProductPrices`

### `Edit([FromBody] ProductPrice model)` — POST (Admin only)
- ค้นหารายการด้วยรหัสสินค้า
- อัปเดตข้อมูลที่เปลี่ยนแปลง

### `Delete(string productCode)` — POST (Admin only)
- ลบรายการออกจากตาราง `ProductPrices`
- ถ้าไม่พบรหัสสินค้า คืนค่า Error

---

## การควบคุมสิทธิ์

- หน้าเว็บ: ปุ่ม เพิ่ม / แก้ไข / ลบ จะแสดงเฉพาะเมื่อ `isAdmin == true`
- หลังบ้าน: ทุก Action ที่แก้ข้อมูลใช้ `[Authorize(Roles = "Admin")]` คุ้มกันไว้

---

## ข้อควรรู้

- **Primary Key** ของตาราง `ProductPrices` คือ `ProductCode` (ตัวอักษร) ไม่ใช่ตัวเลข
- ข้อมูลเริ่มต้น Seed มาจากไฟล์ `wwwroot/data/price_per_unit.json`
- ถ้าอยากเพิ่มคอลัมน์ใหม่ ให้แก้ที่ `Models/ProductPrice.cs` แล้วรัน ALTER TABLE ใน MySQL ครับ
