-- ============================================================
-- CostFlow — Reset Data Script
-- ล้างข้อมูลทั้งหมด ยกเว้น Account (AspNet* tables)
-- ============================================================
-- !! ตรวจสอบก่อน run !!
-- ข้อมูลที่ถูกลบจะหายถาวร ไม่สามารถกู้คืนได้
-- ============================================================

SET FOREIGN_KEY_CHECKS = 0;

-- 1. ล้าง Monthly Order Actions (การตัดสินใจรายเดือน)
TRUNCATE TABLE `MonthlyOrderActions`;

-- 2. ล้าง Weekly Plan Details (รายละเอียด PO ในแผนผลิต)
TRUNCATE TABLE `WeeklyPlanDetails`;

-- 3. ล้าง Weekly Plans (ไฟล์แผนผลิตสัปดาห์)
TRUNCATE TABLE `WeeklyPlans`;

-- 4. ล้าง Order Tracking Masters (รายการ PO ทั้งหมด)
TRUNCATE TABLE `OrderTrackingMasters`;

-- 5. ล้าง Reports (ไฟล์สั่งผลิตที่ import มา)
TRUNCATE TABLE `Reports`;

-- 6. ล้าง Product Prices (ตารางราคาสินค้า — จะ sync ใหม่จาก Google Sheets)
TRUNCATE TABLE `ProductPrices`;

SET FOREIGN_KEY_CHECKS = 1;

-- ============================================================
-- ตาราง Account ที่ไม่แตะ (ข้อมูล User/Role ยังอยู่ครบ):
--   AspNetUsers
--   AspNetRoles
--   AspNetUserRoles
--   AspNetUserClaims
--   AspNetRoleClaims
--   AspNetUserLogins
--   AspNetUserTokens
-- ============================================================

SELECT 'Reset complete. Account data preserved.' AS status;
