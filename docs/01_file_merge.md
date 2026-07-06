# ฟีเจอร์ที่ 1 — รวมและเปรียบเทียบไฟล์ Excel

## ทำอะไร
รับไฟล์ Excel 2 ไฟล์จากผู้ใช้ แล้วนำมาเปรียบเทียบกันว่าข้อมูลรายการอะไหล่ตรงกันหรือไม่ และราคาต่างกันเท่าไหร่

## ไฟล์ที่เกี่ยวข้อง

| ไฟล์ | หน้าที่ |
|---|---|
| `Controllers/FileMergeController.cs` | ตัวหลักควบคุมทุกขั้นตอน |
| `Controllers/FileImportController.cs` | รับไฟล์ที่อัปโหลดแล้วเก็บชั่วคราว |
| `Services/ImportStorageService.cs` | บริการเก็บไฟล์ไว้ใน Memory ชั่วคราว (30 นาที) |
| `Services/ImportDbService.cs` | บันทึกผลการเปรียบเทียบลงฐานข้อมูล |
| `Views/FileMerge/Index.cshtml` | หน้าอัปโหลดไฟล์ |
| `Views/FileMerge/ProcessMerge.cshtml` | หน้าแสดงผลเปรียบเทียบ |

---

## ขั้นตอนการทำงาน (Flow)

```
ผู้ใช้อัปโหลดไฟล์ A + B
        ↓
FileImportController รับไฟล์
อ่านข้อมูลด้วย ClosedXML
เก็บไว้ใน ImportStorageService (Memory)
        ↓
ผู้ใช้กด "เปรียบเทียบ"
        ↓
FileMergeController.ProcessMerge()
ดึงไฟล์ A และ B จาก ImportStorageService
        ↓
สร้าง Dictionary จากไฟล์ A (key = เลข PO)
วนลูปไฟล์ B เทียบทีละแถว
คำนวณส่วนต่างราคาและจำนวน
        ↓
แสดงผลตารางเปรียบเทียบบนหน้าจอ
        ↓
ผู้ใช้กด "บันทึกรายงาน"
        ↓
ImportDbService บันทึกลง ImportSessions + MergeResults
```

---

## ฟังก์ชันหลักใน Controller

### `ProcessMerge(Guid sessionIdA, Guid sessionIdB)`
- ดึงข้อมูลไฟล์ทั้ง 2 จาก `ImportStorageService`
- อ่านชีต `mcsAppvProduct` จากไฟล์ A (ถ้าไม่มีใช้ชีตแรก)
- สร้าง Lookup Dictionary จากเลข PO เพื่อเทียบกับไฟล์ B
- ส่งผลลัพธ์ไปแสดงที่ View `ProcessMerge`

### `SaveMergeResult()`
- รับผลการเปรียบเทียบจากหน้าจอ
- บันทึกลงตาราง `ImportSessions` (ข้อมูลหัวรายงาน)
- บันทึกลงตาราง `MergeResults` (รายละเอียดแต่ละแถว)

---

## ข้อควรรู้

- ไฟล์ที่อัปโหลดถูกเก็บใน **Memory เท่านั้น** ไม่บันทึกลงดิสก์
- หาก Session หมดอายุ (เกิน 30 นาที) ต้องอัปโหลดใหม่
- รองรับนามสกุลไฟล์: `.xlsx`, `.xls`, `.csv`
- ไฟล์ A = แผนผลิต / ไฟล์ B = ใบขออนุมัติสั่งผลิต
