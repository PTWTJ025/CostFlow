# คู่มือการใช้งานและ Prompt Guide สำหรับ Impeccable (`/impeccable`)

คู่มือฉบับนี้รวบรวมคำสั่งและตัวอย่าง Prompt สำหรับการใช้สกิล **Impeccable (`/impeccable`)** ในการออกแบบ สร้าง ตรวจสอบ และขัดเกลาหน้าจอผู้ใช้งาน (Frontend / UI / UX) ของระบบ **CostFlow** ให้ได้มาตรฐานระดับมืออาชีพ โดยแบ่งตามวัตถุประสงค์การใช้งานจริง

---

## 1. เมื่อต้องการสร้างหน้าจอหรือฟีเจอร์ใหม่ (Build & Plan)

ใช้เมื่อต้องการวางโครงสร้าง ออกแบบ หรือพัฒนา UI ใหม่ตั้งแต่เริ่มต้น

| คำสั่ง | วัตถุประสงค์ | ตัวอย่าง Prompt ที่นำไปใช้ได้ทันที |
| :--- | :--- | :--- |
| `/impeccable craft [target]` | วางแผน UX/UI และสร้างหน้าจอ/ฟีเจอร์ใหม่แบบครบวงจรตั้งแต่ต้นจนจบ | `/impeccable craft หน้าต่าง Modal สำหรับมัดรวมไฟล์ (File Merge UI) พร้อมแสดงสถานะ Progress และตารางพรีวิวข้อมูล` |
| `/impeccable shape [target]` | วางแผนโครงสร้าง Information Architecture, User Flow และ Wireframe ก่อนเขียนโค้ด | `/impeccable shape หน้า Dashboard รายงานต้นทุนย้อนหลัง 3 ปี สำหรับผู้บริหาร` |
| `/impeccable document` | สกัดระบบดีไซน์ (Colors, Fonts, Components) จากโค้ดที่มีอยู่เพื่อสร้างไฟล์ `DESIGN.md` | `/impeccable document` |
| `/impeccable extract [target]` | ดึงสไตล์และคอมโพเนนต์ที่ใช้ซ้ำได้มาจัดเก็บเป็น Design Token กลาง | `/impeccable extract Views/Shared/_OrderDetailModal.cshtml เพื่อสร้างมินิคอมโพเนนต์ตารางและปุ่มมาตรฐาน` |

---

## 2. เมื่อต้องการตรวจสอบและประเมินระบบ (Evaluate & Audit)

ใช้เมื่อมีหน้าจอเดิมอยู่แล้ว และต้องการหาจุดอ่อน ตรวจสอบมาตรฐานทางเทคนิค หรือขอคำวิจารณ์เชิงลึก

| คำสั่ง | วัตถุประสงค์ | ตัวอย่าง Prompt ที่นำไปใช้ได้ทันที |
| :--- | :--- | :--- |
| `/impeccable critique [target]` | ประเมินรีวิว UX/UI พร้อมให้คะแนน (Heuristic Scoring) ด้านความง่ายในการใช้งานและการสื่อสาร | `/impeccable critique Views/Shared/_Layout.cshtml วิเคราะห์จุดอ่อนด้าน Visual Hierarchy และการนำทางของ Side Nav` |
| `/impeccable audit [target]` | ตรวจสอบคุณภาพทางเทคนิคเชิงลึก 3 ด้าน: Accessibility (a11y), Performance, Responsive | `/impeccable audit Views/Shared/_OrderDetailModal.cshtml ตรวจสอบอัตราส่วนคอนทราสต์สี การรองรับหน้าจอแท็บเล็ต และการโหลดข้อมูล` |

---

## 3. เมื่อต้องการปรับแต่งและขัดเกลา UI ให้สมบูรณ์ (Refine & Polish)

ใช้เมื่อหน้าจอทำงานได้แล้ว แต่ต้องการยกระดับความเนี๊ยบ ดูเป็นมืออาชีพ และพร้อมขึ้นสู่ Production

| คำสั่ง | วัตถุประสงค์ | ตัวอย่าง Prompt ที่นำไปใช้ได้ทันที |
| :--- | :--- | :--- |
| `/impeccable polish [target]` | เก็บรายละเอียดครั้งสุดท้าย (Micro-interactions, Spacing, Alignment) ก่อนส่งมอบงาน | `/impeccable polish Views/Shared/_Layout.cshtml ให้ปุ่ม เมนู และตารางมีจังหวะ Hover/Active ที่นุ่มนวลและสม่ำเสมอ` |
| `/impeccable distill [target]` | ตัดสิ่งที่ไม่จำเป็นออก ลดความซับซ้อนของหน้าจอ (Minimal & Functional) | `/impeccable distill หน้าตารางแสดงรายการสั่งซื้อ ตัดเส้นขอบและปุ่มที่ซ้ำซ้อนออก ให้เหลือเฉพาะข้อมูลที่สำคัญต่อการตัดสินใจ` |
| `/impeccable harden [target]` | เสริมความแข็งแกร่ง รองรับ Error States, Empty States และ Edge Cases ที่อาจเกิดขึ้นจริง | `/impeccable harden ฟอร์มอัปโหลดไฟล์ Excel เพิ่มสถานะกรณีอัปโหลดไฟล์ผิดประเภท ไฟล์เสียหาย หรือข้อมูลตัวเลขไม่ครบถ้วน` |
| `/impeccable onboard [target]` | ออกแบบประสบการณ์ผู้ใช้ครั้งแรก (First-run flow) และหน้าจอว่างเปล่า (Empty state) ที่ช่วยสอนผู้ใช้ | `/impeccable onboard หน้าประวัติการสั่งซื้อ (กรณีที่ผู้ใช้ยังไม่เคยนำเข้าไฟล์ใดๆ) ให้มีปุ่มและคำอธิบายสั้นๆ ชวนกดนำเข้า` |
| `/impeccable bolder [target]` | เพิ่มความโดดเด่นและพลังให้กับดีไซน์ที่ดูเรียบหรือชืดเกินไป | `/impeccable bolder ป้าย Hero Metric สรุปงบประมาณรวม ให้ดูน่าสนใจและดึงดูดสายตาผู้บริหารมากขึ้น` |
| `/impeccable quieter [target]` | ลดความฉูดฉาดหรือความรกเกะกะ ให้หน้าจอมีความสงบ เรียบร้อย สบายตา | `/impeccable quieter ตารางข้อมูลที่มีสีแจ้งเตือนหลายสีจนแสบตา ปรับให้คุมโทน Navy และใช้สีเฉพาะจุดที่ผิดปกติจริง` |

---

## 4. เมื่อต้องการเพิ่มความสวยงาม ลูกเล่น และการจัดวาง (Enhance & Delight)

ใช้เจาะจงปรับปรุงเฉพาะด้าน เช่น สีสัน ฟอนต์ การเว้นระยะ และอนิเมชั่น

| คำสั่ง | วัตถุประสงค์ | ตัวอย่าง Prompt ที่นำไปใช้ได้ทันที |
| :--- | :--- | :--- |
| `/impeccable layout [target]` | จัดการระยะห่าง (Spacing Scale), Rhythm และ Visual Hierarchy ของหน้าจอ | `/impeccable layout Views/Shared/_OrderDetailModal.cshtml จัดระยะห่างระหว่างหัวข้อและฟอร์มข้อมูลให้อ่านง่าย ไม่อึดอัด` |
| `/impeccable typeset [target]` | ปรับปรุงระบบตัวอักษร ขนาด (Typography Scale), น้ำหนัก และความน่าอ่านของข้อความ | `/impeccable typeset ตารางแสดงผลตัวเลข Unit Cost ปรับฟอนต์และขนาดให้กวาดสายตาเปรียบเทียบราคาได้อย่างรวดเร็ว` |
| `/impeccable colorize [target]` | ปรับระบบสี เพิ่มความหมายให้กับ UI ที่เป็นสีเดียวหรือโทนจืด (Monochromatic) | `/impeccable colorize ป้ายสถานะ (Status Badge) ของใบสั่งซื้อ ให้แยกแยะสถานะ อนุมัติ/รอตรวจสอบ/ยกเลิก ได้ชัดเจน` |
| `/impeccable animate [target]` | เพิ่ม Micro-interaction และ Transition ที่มีความหมาย ไม่เวียนหัว และลื่นไหล | `/impeccable animate ป๊อปอัปและเมนู Dropdown เพิ่มการเปิดปิดแบบ Exponential Ease-out ที่นุ่มนวลและรวดเร็ว` |
| `/impeccable delight [target]` | เติมลูกเล่นและความใส่ใจที่สร้างความประทับใจ (เช่น เสน่ห์ของแมวแอดมินใน CostFlow) | `/impeccable delight จุดบริการช่วยเหลือด้านขวาล่าง เพิ่มอนิเมชั่นทักทายของแมวแอดมินแบบน่ารักและเป็นกันเอง` |

---

## 5. เมื่อต้องการแก้ไขจุดบกพร่องและข้อความ (Fix & Adapt)

ใช้แก้ปัญหาเฉพาะจุด เช่น ข้อความอ่านยาก การแสดงผลบนมือถือ หรือความล่าช้า

| คำสั่ง | วัตถุประสงค์ | ตัวอย่าง Prompt ที่นำไปใช้ได้ทันที |
| :--- | :--- | :--- |
| `/impeccable clarify [target]` | ปรับปรุงคำพูด ข้อความบนปุ่ม (UX Copy) และข้อความแจ้งเตือน Error ให้เข้าใจง่าย ไม่ใช้ศัพท์เทคนิค | `/impeccable clarify ข้อความแจ้งเตือนเมื่อรวมไฟล์ Excel ไม่สำเร็จ เปลี่ยนจากรหัส Error เป็นคำอธิบายที่ช่างและพนักงานเข้าใจได้ทันที` |
| `/impeccable adapt [target]` | ปรับแก้หน้าจอให้รองรับอุปกรณ์ขนาดต่างๆ (Desktop, Tablet หน้างาน, Mobile) | `/impeccable adapt Views/Shared/_Layout.cshtml และตารางข้อมูล ให้แสดงผลบนแท็บเล็ตหน้างานได้อย่างสมบูรณ์ ไม่ล้นจอ` |
| `/impeccable optimize [target]` | วิเคราะห์และแก้ไขปัญหาความล่าช้าของ UI และลดภาระการเรนเดอร์ใน DOM | `/impeccable optimize หน้าตารางข้อมูลรายการสินค้าที่มีมากกว่า 1,000 แถว ให้เลื่อนหน้าจอได้อย่างลื่นไหล ไม่กระตุก` |

---

## 6. เมื่อต้องการปรับแต่งหน้าจอจริงบนเบราว์เซอร์ (Iterate & Live Mode)

| คำสั่ง | วัตถุประสงค์ | ตัวอย่าง Prompt ที่นำไปใช้ได้ทันที |
| :--- | :--- | :--- |
| `/impeccable live` | เปิดโหมด Live Variant เลือกอีลีเมนต์บนเบราว์เซอร์ แล้วให้ระบบสร้าง UI ตัวเลือกสลับผ่าน Dev Server ทันที | `/impeccable live` (รันคำสั่งนี้ขณะที่เปิดแอปบนเบราว์เซอร์ เพื่อคลิกเลือกส่วนที่ต้องการปรับเปลี่ยนในหน้าเว็บได้ทันที) |

---

## 7. เทคนิคการเขียน Prompt คู่กับ `/impeccable` เพื่อผลลัพธ์สูงสุด

ในการสั่งงานแต่ละครั้ง สามารถระบุ **เป้าหมาย (What)** + **เหตุผล (Why/Who)** + **ข้อจำกัด (Constraints)** พ่วงท้ายคำสั่ง เพื่อให้ Agent ทำงานได้อย่างแม่นยำและตรงโจทย์ขององค์กร:

- **ระบุไฟล์เป้าหมายที่ชัดเจน**: เช่น `Views/Shared/_Layout.cshtml` หรือชื่อฟีเจอร์ `File Merge Modal`
- **อ้างอิงถึงกลุ่มผู้ใช้**: เช่น *"สำหรับช่างเทคนิคที่ใช้แท็บเล็ตกลางแจ้ง"* หรือ *"สำหรับผู้บริหารที่ต้องการดูตัวเลขสรุปใน 5 วินาที"*
- **ระบุโทนสีหรือข้อห้าม (Anti-references)**: เช่น *"คงโทนสี Royal Navy Blue ตาม PRODUCT.md และหลีกเลี่ยงการใช้ Glassmorphism"*

**ตัวอย่าง Prompt แบบมืออาชีพ**:
> `/impeccable craft หน้าป๊อปอัปเปรียบเทียบราคากลางสินค้า (Price Reference Modal) สำหรับช่างหน้างาน โดยเน้นแสดงตารางเปรียบเทียบราคาล่าสุด 3 เจ้าให้เห็นตัวเลขชัดเจนที่สุด (Clarity Over Clutter) รองรับการแตะบนหน้าจอแท็บเล็ต และมีปุ่มเลือกรายการที่นุ่มนวลและตอบสนองทันที`

---

## 8. คำสั่งจัดการระบบ (Management Commands)

- **สร้าง Shortcut ย่อคำสั่ง**: `node .kiro/skills/impeccable/scripts/pin.mjs pin <command>` (เช่น `pin audit` เพื่อให้พิมพ์ `/audit` ได้โดยตรง)
- **ยกเลิก Shortcut**: `node .kiro/skills/impeccable/scripts/pin.mjs unpin <command>`
- **จัดการ Git Hooks (ตรวจสอบดีไซน์อัตโนมัติหลังแก้ไฟล์)**: `/impeccable hooks <on|off|status>`

---

## 9. รายละเอียดเชิงลึกของคำสั่งที่สำคัญ

### 9.1 `/impeccable craft` - สร้างฟีเจอร์แบบครบวงจร

**วิธีการทำงาน (4 ระยะ):**

1. **Shape (การออกแบบ)**: ทำ Discovery Interview เพื่อทำความเข้าใจวัตถุประสงค์ ผู้ใช้งาน ข้อจำกัด และทิศทางการออกแบบ
2. **Load References (โหลดข้อมูลอ้างอิง)**: ดึงข้อมูลเกี่ยวกับ Spatial, Typography, Motion, Color, Interaction principles
3. **Build (สร้าง)**: พัฒนาโครงสร้าง hierarchy, type, color, states, motion และ responsive design
4. **Iterate Visually (ปรับแต่งด้วยภาพ)**: ตรวจสอบผลลัพธ์ในเบราว์เซอร์และปรับแต่งจนได้ตามที่ต้องการ

**เมื่อไหร่ควรใช้:**
- กำลังสร้างฟีเจอร์ใหม่ตั้งแต่ศูนย์และต้องการกระบวนการครบวงจร
- รู้ว่าจะสร้างอะไร แต่ยังไม่ชัดเจนว่าควรมีหน้าตาอย่างไร
- ต้องการ visual iteration เป็นค่าเริ่มต้น

**ตัวอย่าง:**
```
/impeccable craft a pricing page for a developer tool
```

คาดหวัง: คำถามค้นพบ 5-10 ข้อ → design brief → การสร้าง → การ iterate หลายรอบ

---

### 9.2 `/impeccable shape` - วางแผนก่อนสร้าง

**วิธีการทำงาน:**

รัน discovery interview แบบมีโครงสร้าง ครอบคลุมประเด็น:
- **Purpose & Context**: วัตถุประสงค์ของฟีเจอร์ ใครใช้ สภาพจิตใจของผู้ใช้
- **Content & Data**: ข้อมูลที่จะแสดง ช่วงข้อมูล edge cases
- **Design Goals**: สิ่งสำคัญที่สุด ความรู้สึกที่ต้องการ ตัวอย่างอ้างอิง
- **Constraints**: ข้อจำกัดด้านเทคนิค เนื้อหา accessibility, localization

**ผลลัพธ์:** ไฟล์ `brief.md` ที่มีโครงสร้างซึ่งสามารถนำไปใช้กับ `/impeccable` หรือ implementation skill อื่นๆ

**ตัวอย่าง:**
```
/impeccable shape a daily digest email preferences page
```

**หมายเหตุ:** ถ้าต้องการทั้ง discovery และสร้างในคำสั่งเดียว ใช้ `/impeccable craft` แทน

---

### 9.3 `/impeccable audit` - ตรวจสอบคุณภาพทางเทคนิค

**5 มิติการตรวจสอบ (คะแนน 0-4 แต่ละมิติ):**

1. **Accessibility**: WCAG contrast, ARIA, keyboard navigation, semantic HTML, form labels
2. **Performance**: Layout thrashing, expensive animations, lazy loading, bundle weight
3. **Theming**: Hard-coded colors, dark mode coverage, token consistency
4. **Responsive**: Breakpoint behavior, touch targets, mobile viewport handling
5. **Anti-patterns**: ตรวจสอบ deterministic checks เหมือน Detector CLI

**Severity Ratings:**
- **P0**: Blocks release (ต้องแก้ก่อนปล่อย)
- **P1**: Fix this sprint (แก้ในรอบนี้)
- **P2**: Next cycle (รอบถัดไป)
- **P3**: Polish (ขัดเกลา)

**ผลลัพธ์:** เอกสารรายงานที่สามารถนำไปใส่ใน ticket tracker ได้เลย

**ตัวอย่าง:**
```
/impeccable audit the checkout flow
```

**ผลลัพธ์ตัวอย่าง:**
```
Accessibility: 2/4 (partial)
  P0: Missing form labels on 4 inputs
  P1: Contrast 3.1:1 on disabled button state
  P2: No visible focus indicator on custom dropdown

Performance: 3/4 (good)
  P1: Hero image not lazy-loaded (340KB)
  ...
```

---

### 9.4 `/impeccable critique` - รีวิวดีไซน์

**วิธีการทำงาน (2 ขั้นตอนแยกอิสระ):**

1. **LLM Design Review**: อ่าน source code, ตรวจสอบหน้าจริงในเบราว์เซอร์ (ถ้าทำได้), วิเคราะห์ตาม DO/DON'T catalog
   - คะแนน Nielsen's heuristics
   - นับ cognitive load failures
   - ตรวจสอบ emotional journey
   - หา AI slop patterns

2. **Automated Detector**: รัน `npx impeccable detect` หา gradient text, purple palettes, side-tab borders, nested cards, line length problems

**ผลลัพธ์:** รายงานที่รวมทั้งสองส่วน:
- AI slop verdict (pass/fail พร้อมหลักฐาน)
- คะแนน Heuristic 10 ข้อ (0-4)
- Cognitive load failure count
- Priority issues (3-5 รายการ)
- คำถามที่ควรตอบก่อน ship

**ตัวอย่าง:**
```
/impeccable critique the homepage hero
```

---

### 9.5 `/impeccable animate` - เพิ่ม Motion ที่มีความหมาย

**หลักการ:**
- Entrances & exits: 200-300ms fades + subtle Y or scale
- State feedback: hover, active, focus, loading, success
- Transitions between views: shared-element หรือ fade-through
- Progress & loading: skeleton screens, progress bars
- **Reduced motion fallback**: ทุก animation ต้องมี `@media (prefers-reduced-motion: reduce)`

**Easing:** เสมอเป็น exponential (ease-out-quart, quint, expo) ไม่มี bounce, elastic

**ข้อจำกัด:** animate เฉพาะ `transform` และ `opacity` เท่านั้น ไม่ใช้ width, height, top, left

**ตัวอย่าง:**
```
/impeccable animate the sign-up flow
```

**การเพิ่มที่คาดหวัง:**
- Email input: focus glow (180ms)
- Submit button: spinner ภายใน (ไม่ใช่ข้างนอก) เมื่อ loading
- Success screen: opacity + translateY(8px), 260ms
- Reduced-motion fallback สำหรับทุก transition

---

### 9.6 `/impeccable bolder` - ทำให้โดดเด่นขึ้น

**4 แกนการขยาย:**

1. **Scale**: Display type → `clamp(3rem, 6vw, 6rem)` หรือมากกว่า
2. **Weight Contrast**: Light 300 vs Heavy 800 (ไม่ใช่ medium vs regular)
3. **Color Commitment**: Accent color แบบเข้มข้น ไม่จาง, พื้นหลังกล้าตัดสิน
4. **Compositional Confidence**: Asymmetry, off-grid, pullquotes, hanging punctuation

**ไม่เหมาะกับ:** Dashboards, operator tools, forms ที่คนจ้องดูหลายชั่วโมง

**ตัวอย่าง:**
```
/impeccable bolder the landing page hero
```

---

### 9.7 `/impeccable quieter` - ลดความฉูดฉาด

**4 แกนการลด:**

1. **Color**: Desaturate, ลด chroma, ใช้ accent เดียว + support ที่เบา
2. **Contrast**: ลด extreme darks/lights, ใช้ paper และ ink แทน pure white/black
3. **Decoration**: เอา shadows, borders, gradients ที่ไม่ได้ทำหน้าที่ออก
4. **Motion & Effect**: ชะลอ animation, เอา auto-play ออก, ลด parallax และ blur

**เก็บไว้:** จุดยืนของดีไซน์ ไม่ใช่ทำให้เป็นกลางโดยสิ้นเชิง

**ตัวอย่าง:**
```
/impeccable quieter the pricing page
```

---

### 9.8 `/impeccable colorize` - เพิ่มสีอย่างมีกลยุทธ์

**วิธีการ:**
- อ่าน brand color ที่มีอยู่
- ตัดสินใจว่าจะใช้สีที่ไหน:
  - Primary action: สีแบรนด์เข้มสุด
  - Secondary accents: muted variants
  - Neutrals: tinted ไปทาง brand hue (chroma ~0.005-0.01)
  - Content categories: accent system จำกัด

**ใช้ OKLCH** เพื่อให้ lightness steps ดูเท่ากัน

**ตัวอย่าง:**
```
/impeccable colorize the dashboard
```

---

### 9.9 `/impeccable delight` - เพิ่มบุคลิกภาพ

**จุดที่มักเพิ่ม:**
- Empty states: ข้อความที่มีเอกลักษณ์
- Loading moments: ข้อความขณะรอ
- Success feedback: animation ฉลอง
- Microcopy: button labels, tooltips, errors
- Easter eggs: สิ่งที่ค้นพบได้

**หลักการ:** ทุก delight moment ต้องทำงานได้ปกติแม้ไม่มี delight

**ตัวอย่าง:**
```
/impeccable delight the first-run experience
```

---

### 9.10 `/impeccable typeset` - ปรับปรุง Typography

**5 มิติการประเมิน:**

1. **Font Choices**: หลีกเลี่ยง defaults (Inter, Roboto), ใช้ไม่เกิน 2-3 families
2. **Hierarchy**: ความแตกต่างชัดเจน, size contrast ≥1.25x ระหว่างระดับ
3. **Sizing & Scale**: type scale สอดคล้อง, body text ≥16px
4. **Readability**: line length 45-75ch, line-height เหมาะกับฟอนต์
5. **Consistency**: element เดียวกันใช้ treatment เดียวกัน

**ตัวอย่าง:**
```
/impeccable typeset the article layout
```

---

### 9.11 `/impeccable layout` - จัดการ Spacing และ Hierarchy

**5 มิติ:**

1. **Spacing**: spacing scale สอดคล้อง, จัดกลุ่ม element ที่เกี่ยวข้อง
2. **Visual Hierarchy**: สายตาลงที่ primary action ภายใน 2 วินาที
3. **Grid & Structure**: มี underlying grid, align ตาม baselines
4. **Rhythm**: สลับระหว่าง tight และ generous spacing
5. **Density**: ความหนาแน่นเหมาะกับประเภทเนื้อหา

**ตัวอย่าง:**
```
/impeccable layout the settings page
```

---

### 9.12 `/impeccable clarify` - ปรับปรุง UX Copy

**พื้นที่ที่มักเขียนใหม่:**

- **Labels & Field Hints**: ตรงไปตรงมา เฉพาะเจาะจง บอกว่าคาดหวังอะไร
- **Button Copy**: เริ่มด้วย verb, บรรยายผลลัพธ์ ไม่ใช่การกระทำ ("Save changes" ไม่ใช่ "OK")
- **Error Messages**: อธิบายว่าเกิดอะไร ใครผิด ทำอะไรต่อ ไม่โทษผู้ใช้
- **Empty States**: บอกทิศทาง อธิบายว่าทำไมว่าง เสนอขั้นตอนต่อไป
- **Tooltips**: เพิ่มข้อมูลที่ label ใส่ไม่ได้ ไม่ repeat
- **Confirmation Dialogs**: ระบุผลที่ตามมา ไม่ใช่แค่การกระทำ

**ปรับ voice ตาม audience ใน PRODUCT.md**

**ตัวอย่าง:**
```
/impeccable clarify the billing form
```

**ก่อน/หลัง:**
- Label: "Billing address" → "Address on your card"
- Error: "Invalid input" → "This card number is 15 digits. You entered 14."
- Button: "Submit" → "Charge $29 and subscribe"

---

### 9.13 `/impeccable adapt` - ปรับให้รองรับหลายบริบท

**4 มิติ:**

1. **Breakpoints & Fluid Layout**: ยุบ multi-column เป็น single, ปรับ clamp ranges
2. **Touch Targets**: minimum 44px, เว้นระยะพอระหว่าง targets
3. **Navigation Patterns**: sidebar → bottom nav หรือ slide-out, collapse toolbars
4. **Content Priority**: สิ่งที่ต้องเห็น, สิ่งที่ collapse ได้, สิ่งที่เอาออกได้

**กฎเหล็ก:** Adapt ไม่ใช่ Amputate - ฟีเจอร์สำคัญต้องไม่หายบน mobile

**ตัวอย่าง:**
```
/impeccable adapt the settings page for mobile
```

---

### 9.14 `/impeccable overdrive` - ผลักดันเกินขีดจำกัด

**เทคนิค:**
- WebGL shaders
- Spring physics
- Scroll Timeline API
- View Transitions
- Canvas animation
- GPU-accelerated filters

**คำเตือน:** ใช้เมื่อ budget และโอกาสเหมาะสม ไม่ใช่สำหรับ operator tools

**Output ประกาศด้วย:** `──── ⚡ OVERDRIVE ────`

**ตัวอย่าง:**
```
/impeccable overdrive the landing hero
```

---

## 10. หลักการออกแบบทั่วไป (General Design Rules)

### Color
- **ตรวจสอบ contrast**: Body text ≥4.5:1, Large text ≥3:1
- Gray บนพื้นหลังสีดูซีด ใช้ darker shade ของสีพื้นหลังแทน

### Typography
- **Line length**: 65-75 characters
- **Display heading max**: clamp() ≤ 6rem (~96px)
- **Letter-spacing floor**: ≥ -0.04em
- ใช้ `text-wrap: balance` สำหรับ h1-h3, `text-wrap: pretty` สำหรับ prose

### Layout
- **Flexbox for 1D, Grid for 2D**
- Responsive grids: `repeat(auto-fit, minmax(280px, 1fr))`
- สร้าง semantic z-index scale (dropdown → sticky → modal → toast)

### Motion
- ไม่ animate CSS layout properties เว้นแต่จำเป็น
- ใช้ exponential curves (ease-out-quart/quint/expo)
- **Reduced motion**: ทุก animation ต้องมี fallback
- Reveal animations ต้อง enhance ของที่มองเห็นแล้ว ไม่ block visibility

### Interaction
- Dropdowns ใน `overflow: hidden` container จะถูก clip → ใช้ `<dialog>`, popover API, หรือ portal

---

## 11. สิ่งที่ห้ามทำโดยเด็ดขาด (Absolute Bans)

❌ **Side-stripe borders** (border-left/right > 1px เป็น accent)  
❌ **Gradient text** (background-clip: text)  
❌ **Glassmorphism as default**  
❌ **Hero-metric template** (big number + small label + gradient)  
❌ **Identical card grids**  
❌ **Tiny uppercase tracked eyebrow** บนทุก section (01 · About / 02 · Process)  
❌ **Numbered section markers as default** เว้นแต่เป็น sequence จริงๆ  
❌ **Text overflow** จาก container

---

## 12. การทดสอบ AI Slop

**First-order check:** ถ้าใครทายชุดสี/theme จาก category ได้ → เป็น training-data reflex  
**Second-order check:** ถ้าทาย aesthetic family จาก "category + anti-references" ได้ → trap ระดับ 2

ปรับ scene sentence และ color strategy จนคำตอบไม่ชัดเจน

---

## 13. คำสั่งเพิ่มเติม (Advanced Commands)

### 13.1 `/impeccable distill` - ตัดทิ้งสิ่งที่ไม่จำเป็น

**Ruthless subtraction: ลดจนเหลือแต่สาระสำคัญ**

**วิธีการทำงาน (2 passes):**

1. **ประเมินแหล่งที่มาของความซับซ้อน:**
   - Element มากเกินไป
   - Variation มากเกินไป
   - Information overload
   - Visual noise
   - Hierarchy สับสน
   - Feature creep

2. **Edit อย่างไร้ความปรานี:**
   - เอาสิ่งที่ไม่จำเป็นออก
   - รวมสิ่งที่รวมได้
   - ซ่อนสิ่งที่รอได้
   - รวม variation เป็น single treatment
   - Commit กับ visual language เดียว

**หลักการ:** ทุก element ต้องพิสูจน์การมีอยู่ของมัน ลดอุปสรรค ไม่ใช่ลดฟีเจอร์

**ตัวอย่าง:**
```
/impeccable distill this dashboard
```

**ก่อน/หลัง:**
- 4 card styles → 1 style
- 3 button variants → 1 variant (demote อื่นเป็น text links)
- 2 header treatments → 1 unified header
- Sidebar 14 items / 5 sections → 3 sections
- Advanced options → ซ่อนใน disclosure

**Pitfalls:**
- สับสน distill กับ delete: distill เอาอุปสรรคออก ไม่ใช่เอาฟีเจอร์ที่ใช้ทุกวันออก
- รันเร็วเกินไป: ถ้าฟีเจอร์ยังเติบโต รอจนรูปร่างมั่นคง
- คาดหวังให้แทน hierarchy work: บางทีปัญหาคือการจัดวาง ไม่ใช่จำนวน → ใช้ `/impeccable layout`

---

### 13.2 `/impeccable harden` - เตรียมพร้อม Production

**ทำให้ interface รับมือกับความเป็นจริงได้**

**4 มิติของความแข็งแกร่ง:**

1. **Text & Data Extremes:**
   - ข้อความยาว/สั้น
   - อักขระพิเศษ, emoji, RTL
   - ตัวเลขหลักพัน/หลักล้าน
   - รายการ 1,000 items

2. **Error Scenarios:**
   - Network failures
   - API 4xx/5xx
   - Validation errors
   - Permission errors
   - Rate limits
   - Concurrent operations

3. **Internationalization:**
   - แปลภาษาที่ยาวกว่า (เยอรมันยาวกว่าอังกฤษ 30%)
   - ภาษา RTL
   - รูปแบบวันที่และตัวเลข
   - สกุลเงิน
   - Character sets

4. **Device & Context:**
   - Touch targets
   - Offline behavior
   - การเชื่อมต่อช้า
   - Low-power mode

**ตัวอย่าง:**
```
/impeccable harden the user profile page for long names
```

**ผลลัพธ์:**
- `.user-name`: เพิ่ม `text-overflow: ellipsis` + tooltip
- `bio`: เปลี่ยนจาก fixed height → max-height + "show more"
- เพิ่ม empty state สำหรับ user ที่ไม่มี bio
- เพิ่ม skeleton loader สำหรับ async avatar fetch
- ทดสอบที่ name lengths: 1, 20, 60, 200 ตัวอักษร

**Pitfalls:**
- รอ bug report: harden เป็นการป้องกัน
- ถือว่า error/empty states เป็นเรื่องรอง: ส่วนใหญ่ของ hardening คือ error/empty state UI
- ข้าม i18n เพราะ "เราใช้แค่ภาษาอังกฤษ": i18n-safe layouts ดีกว่าอยู่แล้ว

---

### 13.3 `/impeccable onboard` - ออกแบบประสบการณ์ครั้งแรก

**First-run experiences, empty states, และเส้นทางสู่คุณค่า**

**วิธีการทำงาน:**

เริ่มจากคำถาม: **aha moment คืออะไร และผู้ใช้ใหม่จะไปถึงตรงนั้นได้เร็วแค่ไหน**

**ครอบคลุมพื้นที่:**

1. **First-run Experience:** ทันทีหลัง sign-up
   - Tour, blank canvas, filled example, หรือไม่มีอะไรเลย
   - เลือกแนวทางที่เข้ากับผลิตภัณฑ์

2. **Empty States:** ทุกหน้าจอที่ไม่มีข้อมูล
   - ฉันอยู่ไหน
   - ทำไมว่างเปล่า
   - ทำอะไรต่อ
   - จะมีหน้าตาอย่างไรเมื่อเต็ม

3. **Setup & Installation:**
   - ลด required configuration
   - Defaults ฉลาด
   - แต่ละ step อธิบายว่าทำไมสำคัญ

4. **Progressive Disclosure:**
   - ฟีเจอร์ขั้นสูงซ่อนจนกว่าจะถึงเวลา

5. **Activation Events:**
   - Instrument และฉลองครั้งแรกที่ได้ core value แบบเงียบๆ

**ตัวอย่าง:**
```
/impeccable onboard the editor
```

**ผลลัพธ์:**
- First-run: แทน empty editor ด้วย example document ที่แก้ไขได้
- Empty state: "No documents yet. Create your first, or import from..."
- Setup: ลดจาก 6 fields → 1 field (workspace name), ที่เหลือมี smart defaults
- Activation: ครั้งแรกที่ save → quiet toast "Saved. Your work is in the cloud now."

**Pitfalls:**
- เพิ่ม product tour เป็นคำตอบ default: ส่วนใหญ่ไม่ต้องการ tour แค่ต้องการ first screen ที่ดีกว่า
- ออกแบบ onboarding โดยไม่กำหนด aha moment
- รัน onboard บน broken flow: แก้ flow ก่อน

---

### 13.4 `/impeccable optimize` - แก้ปัญหา Performance

**5 มิติการ optimize:**

1. **Loading & Web Vitals:** LCP, INP, CLS
2. **Rendering:** Re-renders, memoization, reconciliation, layout thrash
3. **Animations:** Layout properties, transforms/opacity, will-change
4. **Images & Assets:** Lazy loading, responsive images, modern formats
5. **Bundle Size:** Unused imports, code-splitting, dead code

**ทุกการแก้ไขต้องวัดผล ถ้าไม่เปลี่ยนอะไร → rollback**

**ตัวอย่าง:**
```
/impeccable optimize the homepage
```

**ผลลัพธ์ตัวอย่าง:**
```
LCP: 3.2s → 1.4s
  - Hero image preloaded (-800ms)
  - Removed render-blocking font stylesheet (-240ms)
  - Deferred analytics script (-180ms)

INP: 240ms → 90ms
  - Debounced scroll handler
  - Memoized expensive list render
  - Removed synchronous layout read

CLS: 0.18 → 0.02
  - Set dimensions on hero image
  - Reserved space for async header badge

Bundle: 340KB → 180KB
  - Removed unused lodash (52KB)
  - Code-split playground route (78KB)
  - Dropped deprecated icon set (30KB)
```

**Pitfalls:**
- Optimize ก่อนวัด: ไม่มี baseline ไม่รู้ว่าอะไรช่วย
- ไล่ตาม tiny wins: 20ms improvement ที่ใช้เวลาสัปดาห์ไม่คุ้ม
- ลืมวัดใหม่หลังทุกการเปลี่ยน

---

### 13.5 `/impeccable polish` - ขัดเกลาครั้งสุดท้าย

**จาก good → great ก่อน ship**

**6 มิติของ Polish:**

1. **Visual Alignment & Spacing:** pixel-perfect grid, consistent spacing scale
2. **Typography:** hierarchy consistency, line length, widows/orphans
3. **Color & Contrast:** token usage, theme parity, WCAG ratios
4. **Interaction States:** hover, focus, active, disabled, loading, error, success
5. **Transitions & Motion:** smooth easing, no jank, reduced-motion
6. **Copy:** consistent voice, correct tense, no placeholders/TODOs

**หลักการ:** Polish เป็นขั้นตอนสุดท้าย ไม่ใช่แรก

**ตัวอย่าง:**
```
/impeccable polish the pricing page
```

**ผลลัพธ์ตัวอย่าง:**
- Fixed 3 off-grid elements (8px baseline)
- Tightened h1 kerning, fixed widow on testimonial
- Added hover state on FAQ items
- Softened modal entrance, added reduced-motion fallback
- Removed "Lorem ipsum", aligned button voice

**Pitfalls:**
- Polish งานที่ยังไม่เสร็จ: ถ้ามี TODOs ในโค้ด ยังไม่พร้อม
- ถือว่า polish เป็น redesign: polish ปรับแต่งของที่มี ไม่ใช่ rearchitect
- รัน polish โดยไม่รัน audit ก่อน: ใช้ทั้งคู่

---

### 13.6 `/impeccable document` - สร้าง DESIGN.md

**Generate spec ที่บันทึกระบบ visual เพื่อให้ AI agent ทุกตัวอยู่ในแบรนด์**

**6 ส่วนคงที่ (ตามลำดับ):**

1. **Overview**: Creative North Star
2. **Colors**: Palette และ strategy
3. **Typography**: Fonts และ scale
4. **Elevation**: Shadow และ depth philosophy
5. **Components**: ตัวอย่าง component patterns
6. **Do's and Don'ts**: กฎที่ทำได้/ห้าม

**พร้อมไฟล์ sidecar:** `.impeccable/design.json` (machine-readable)

**วิธีการทำงาน:**

Scan codebase หา design assets:
- CSS custom properties
- Tailwind config
- CSS-in-JS themes
- Design token files
- Component source
- Global stylesheet
- Computed styles (ถ้ามี browser)

**ตัวอย่าง:**
```
/impeccable document
```

**สำหรับโปรเจคใหม่:**
```
/impeccable document --seed
```

5 คำถาม, 5 นาที → สร้าง scaffold ที่มี `<!-- SEED -->` comment

**Pitfalls:**
- รันเร็วเกินไป: ถ้าไม่มี tokens ให้ใช้ `--seed` mode
- ถือว่า DESIGN.md เป็น documentation สำหรับคนเท่านั้น: มันสำหรับ AI เป็นหลัก
- เพิ่ม Layout/Motion section เอง: spec มี 6 sections คงที่

---

### 13.7 `/impeccable extract` - ดึง Reusable Components

**Pull tokens และ patterns เข้า design system**

**3 ขั้นตอน:**

1. **Discover Drift:** หา repeated values, button variants, spacing, text styles
2. **Propose Primitives:** ชื่อ token, component APIs
3. **Migrate Call Sites:** แทนที่ duplicated CSS ด้วย primitives ใหม่

**กฎ:** Extract เฉพาะสิ่งที่ใช้ **3+ ครั้ง** ด้วย intent เดียวกัน

**ตัวอย่าง:**
```
/impeccable extract the button styles
```

**ผลลัพธ์:**
- พบ 14 button instances ใน 8 files
- 4 variants: primary, secondary, ghost, destructive
- Extract เป็น `<Button variant="primary" size="default">`
- Migrate 14 call sites, ลบ ~180 lines ของ duplicated CSS
- เพิ่ม 3 tokens: `--button-radius`, `--button-padding-y`, `--button-padding-x`

**Pitfalls:**
- Extract เร็วเกินไป: 2 usages ไม่ใช่ pattern
- Over-generalize: component ควรตรงกับ use cases ปัจจุบัน
- ลืม migrate: extraction โดยไม่ migrate ทิ้ง duplicate code ไว้

---

### 13.8 `/impeccable init` - Setup โปรเจค

**สร้าง context, live mode config, และแนะนำขั้นตอนต่อไป**

**สิ่งที่ init สร้าง:**

1. **PRODUCT.md** (Strategy):
   - Audience
   - Product purpose
   - Voice
   - Anti-references
   - Register (brand/product)

2. **DESIGN.md** (Visual):
   - Colors
   - Typography
   - Elevation
   - Components
   - Rules

3. **Live Mode Config:**
   - Pre-configure framework
   - Entry files

**วิธีการทำงาน:**

Scan codebase → ถาม core choice: **Brand หรือ Product?**

- **Brand**: Landing pages, marketing (impression IS the product)
- **Product**: App UI, dashboards, tools (design SERVES the product)

**ตัวอย่าง:**
```
/impeccable init
```

คาดหวัง: 5-8 นาทีสัมภาษณ์ → สร้างไฟล์ → แนะนำคำสั่งถัดไป

**Pitfalls:**
- ข้ามเพื่อ "ลองคำสั่งเร็วๆ": คำสั่งอื่นจะสัมภาษณ์กลางทาง ช้ากว่า
- ให้คำตอบทั่วไป: "Modern and clean" ไม่เป็นประโยชน์ ต้องเฉพาะเจาะจง
- ถือว่า PRODUCT.md ไม่เปลี่ยน: แก้ได้ทุกเมื่อ

---

### 13.9 `/impeccable live` - Iterate ในเบราว์เซอร์

**Pick element → Drop comment → Get 3 variants → Accept one → Writes to source**

**วิธีใช้งาน:**

1. รัน dev server
2. `/impeccable live`
3. Pick element บนหน้า
4. พิมพ์ description หรือเลือก action chip
5. ดู 3 variants
6. กด arrow keys สลับ
7. Accept หรือ discard

**รองรับ:**
- Vite
- Next.js (+ monorepos)
- SvelteKit
- Astro
- Nuxt
- Static HTML

**ตัวอย่าง:**

Pick newsletter card → click "delight" chip → Go → ได้ 3 variants:
- Stamp-and-postcard feel
- Typographic-surprise version
- Illustrated-accent one

**Pitfalls:**
- รันบนหน้าที่ยังไม่เสร็จ: ต้องมี content จริง ไม่ใช่ Lorem ipsum
- คาดหวังให้ตัดสิน macro decisions: live iterate element เดียว ไม่ใช่ทั้งหน้า
- ไม่มี PRODUCT.md/DESIGN.md: จะได้ variants ทั่วไป ไม่ตรงแบรนด์

---

## 14. สรุปการเลือกใช้คำสั่ง

### เมื่อเริ่มโปรเจคใหม่:
1. `/impeccable init` → สร้าง context
2. `/impeccable document` → บันทึก design system
3. `/impeccable craft [feature]` → สร้างฟีเจอร์แรก

### เมื่อมีโค้ดอยู่แล้วและต้องการปรับปรุง:
1. `/impeccable critique [target]` → รีวิว UX
2. `/impeccable audit [target]` → ตรวจเทคนิค
3. `/impeccable polish [target]` → ขัดเกลาก่อน ship

### เมื่อต้องการเพิ่มคุณภาพเฉพาะด้าน:
- **สี:** `/impeccable colorize`
- **ตัวอักษร:** `/impeccable typeset`
- **ระยะห่าง:** `/impeccable layout`
- **ความเคลื่อนไหว:** `/impeccable animate`
- **บุคลิกภาพ:** `/impeccable delight`

### เมื่อต้องการแก้ปัญหา:
- **ข้อความสับสน:** `/impeccable clarify`
- **ไม่รองรับมือถือ:** `/impeccable adapt`
- **ช้า:** `/impeccable optimize`
- **ไม่แข็งแรง:** `/impeccable harden`

### เมื่อต้องการปรับโทน:
- **เรียบเกินไป:** `/impeccable bolder`
- **ฉูดฉาดเกินไป:** `/impeccable quieter`
- **ซับซ้อนเกินไป:** `/impeccable distill`

---

## 15. Best Practices สำหรับการใช้งานใน CostFlow

### ✅ **Do:**
- รัน `/impeccable init` ครั้งเดียวตอนเริ่มต้น
- ระบุไฟล์เป้าหมายชัดเจน (เช่น `Views/MonthlyExpense/Estimation.cshtml`)
- อ้างอิงถึงผู้ใช้งานจริง (ช่างเทคนิค, ผู้บริหาร)
- ระบุข้อจำกัด (หน้าจอแท็บเล็ต, WCAG AA)
- รัน `/impeccable audit` ก่อน `/impeccable polish`
- ใช้ `/impeccable live` สำหรับ visual iteration

### ❌ **Don't:**
- อย่ารัน polish ก่อนงานเสร็จ
- อย่าข้าม init แล้วคาดหวังผลลัพธ์ที่ตรงแบรนด์
- อย่าใช้ distill เมื่อปัญหาคือ layout
- อย่าใช้ bolder กับ operator tools
- อย่าใช้ optimize ก่อนวัดปัญหา
- อย่าใช้ live mode บนหน้าที่มี Lorem ipsum
