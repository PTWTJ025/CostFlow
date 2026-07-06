# ฟีเจอร์ที่ 3 — คีย์ข้อมูลใบสั่งซื้ออะไหล่

## ทำอะไร
ให้พนักงานกรอกรายการอะไหล่ที่สั่งซื้อทีละรายการ โดยมีระบบค้นหาราคากลางอัตโนมัติขณะพิมพ์ และบันทึกเป็นชุดใบสั่งซื้อ (Batch) เก็บไว้ในฐานข้อมูล

## ไฟล์ที่เกี่ยวข้อง

| ไฟล์ | หน้าที่ |
|---|---|
| `Controllers/ProductSearchController.cs` | ตัวหลักควบคุมทุกอย่างในหน้านี้ |
| `Views/ProductSearch/Index.cshtml` | หน้ากรอกข้อมูลใบสั่งซื้อ |
| `Views/ProductSearch/SavedOrders.cshtml` | หน้ารายการ Batch ที่บันทึกแล้ว |
| `Views/ProductSearch/SavedBatchDetails.cshtml` | หน้าดูรายละเอียดใน Batch |
| `Models/SparePartOrder.cs` | โมเดลข้อมูลรายการสั่งซื้อ |
| `Models/SparePartOrderBatch.cs` | โมเดลข้อมูลหัวใบสั่งซื้อ (Batch) |

---

## ขั้นตอนการทำงาน (Flow)

```
พนักงานเปิดหน้า "คีย์ใบสั่งซื้อ"
        ↓
พิมพ์รหัสสินค้าหรือชื่ออะไหล่
        ↓
JavaScript ส่งคำขอไปยัง /ProductSearch/Suggest (AJAX)
ProductSearchController.Suggest() ค้นจาก ProductPrices
ส่งชื่อ + หน่วย + ราคากลางกลับมาแสดงในฟอร์มอัตโนมัติ
        ↓
พนักงานกรอกจำนวน กดปุ่ม "เพิ่มรายการ"
ระบบเพิ่มแถวลงในตาราง (เก็บใน Session ชั่วคราวในเบราว์เซอร์)
        ↓
พนักงานกด "บันทึกชุดใบสั่งซื้อ"
        ↓
ProductSearchController.SaveBatch() รับข้อมูล JSON จากหน้าบ้าน
สร้าง SparePartOrderBatch (หัวใบ) บันทึกลงฐานข้อมูล
สร้าง SparePartOrder (รายการ) ทีละรายการ บันทึกลงฐานข้อมูล
```

---

## ฟังก์ชันหลักใน Controller

### `Suggest(string q)` — GET /ProductSearch/Suggest
- ค้นหาจากตาราง `ProductPrices` ด้วย keyword
- ค้นทั้งจากรหัสสินค้าและชื่ออะไหล่
- คืนค่าไม่เกิน 15 รายการเป็น JSON

### `SaveBatch([FromBody] SaveBatchRequest request)` — POST /ProductSearch/SaveBatch
- รับข้อมูลเป็น JSON จาก JavaScript ฝั่งหน้าบ้าน
- ตรวจสอบว่ามีรายการก่อนบันทึก
- บันทึก Batch หัวใบก่อน แล้วค่อยบันทึกรายการย่อยทีละรายการ
- คืนค่าเป็น `{ success: true }` หรือ Error Message

### `SavedOrders()` — GET /ProductSearch/SavedOrders
- แสดงรายการ Batch ทั้งหมดของพนักงานที่ล็อกอินอยู่
- Admin จะเห็นทุก Batch ของทุกคน
- Staff จะเห็นเฉพาะ Batch ของตัวเอง

### `DeleteBatch(Guid batchId)` — POST /ProductSearch/DeleteBatch
- ลบ Batch และรายการย่อยทั้งหมดพร้อมกัน
- ตรวจสอบก่อนว่าเจ้าของ Batch ตรงกับคนที่ล็อกอินอยู่

### `EditBatch(Guid batchId)` — POST /ProductSearch/EditBatch
- ดึงข้อมูลเก่ามาโหลดใหม่ในหน้ากรอก
- พนักงานแก้ไขแล้วกดบันทึก จะลบรายการเดิมทิ้งแล้วสร้างใหม่ทั้งหมด

---

## ข้อควรรู้

- ข้อมูลในตารางหน้าจอก่อนกดบันทึก เก็บใน **JavaScript Array** ในเบราว์เซอร์เท่านั้น ถ้าปิดหน้าก่อนบันทึกข้อมูลจะหายครับ
- พนักงาน Staff ไม่สามารถแก้ไขหรือลบข้อมูลของคนอื่นได้ (มีการตรวจ `UserId` ทุกครั้ง)
- ราคากลางที่ดึงมาอัตโนมัติ คือค่าจากตาราง `ProductPrices` แต่พนักงานแก้ไขราคาที่ซื้อจริงได้ก่อนบันทึก
