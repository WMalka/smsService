<div dir="rtl">

# חלק ב' – מסמך הרחבה

המסמך מתייחס לקוד בחלק א'. הפניות לקוד הן קישורים לקובץ ולשורה, למשל [DispatchPending.cs:27](src/Sms.Core/Application/DispatchPending.cs#L27).

## 1. ספק שני

- **ספק נוסף:** נוסיף מחלקה ו-DTOs ייעודיים לספק החדש, שמממשת את `ISmsProvider`. הממשק עצמו, ה-use cases והדומיין לא משתנים.
- **`ProviderName`:** נוסיף עמודה ב-`MessageRecipient`, כדי שבדיקת המסירה תשאל את הספק ששלח את ההודעה. מזהה הודעה של ספק א' לא אומר כלום לספק ב'.
- **`SmsProviderRouter`:** נוסיף נתב שמנווט בין הספקים ונרשם ב-`DependencyInjection.cs` במקום הספק היחיד. `DispatchPending` לא משתנה.
- **בחירת ספק:** הספק הזול יוגדר כ-default, ונגדיר תנאים למעבר לספק השני, למשל כשעוברים את סף היכולת של הראשי.
- **ספק שנופל:** נוסיף circuit breaker לכל ספק (Polly, לא קיים היום בקוד). אחרי כמה כשלים ברצף מפסיקים לפנות לספק לזמן קצר, והנתב שולח לספק השני.
- **timeout:** נוודא שבמקרה של timeout לא נעביר את ההודעה לספק השני, כדי למנוע כפילויות. לא ידוע אם הספק הראשון כבר שלח אותה, והספק השני לא יזהה את ה-`reference` שלנו. במקרה כזה ננסה שוב באותו ספק.

## 2. עומסים

**איפה הקוד נשבר ראשון:** בקצב השליחה. יש worker אחד עם עד 10 שליחות במקביל (`MaxParallelSends`), כלומר כ-100 הודעות בשנייה וכ-50 דקות ל-300,000 הודעות. מיד אחריו SQLite, שמאפשר כותב אחד בכל רגע.

**שינויים, לפי הסדר:**
1. נעלה את `MaxParallelSends` ואת `BatchSize` עד מגבלת הקצב של הספק. זה שינוי הגדרות בלבד, בקטע `Dispatcher` בקובץ `src/Sms.Api/appsettings.json` (או במשתני סביבה, למשל `Dispatcher__MaxParallelSends`).
2. נחליף את SQLite ב-PostgreSQL, כדי לאפשר כתיבות רבות במקביל וכמה workers שלא לוקחים את אותה הודעה (`FOR UPDATE SKIP LOCKED`).
3. נוסיף RabbitMQ רק אם גם זה לא מספיק, כדי לחלק את העבודה בין workers רבים. הטבלה נשארת בתור outbox, כדי לא לאבד הודעות.

**נימוק:** התקרה האמיתית היא מגבלת הקצב של הספק, לא הקוד שלנו. לכן משנים רק עד שמגיעים אליה.

## 3. תקלות

רוב המקרים כבר מטופלים בקוד הנוכחי:

- **השרת קורס באמצע שליחה:**
  - ההודעה נשמרת במסד הנתונים לפני שה-API מחזיר 202, כך שהיא לא הולכת לאיבוד.
    - בקוד: [MessagesController.cs:35](src/Sms.Api/MessagesController.cs#L35) מחכה לשמירה ורק אז מחזיר 202 ([שורה 37](src/Sms.Api/MessagesController.cs#L37)). השמירה עצמה ב-[SendMessage.cs:19](src/Sms.Core/Application/SendMessage.cs#L19) וב-[MessageRepository.cs:23](src/Sms.Infrastructure/Persistence/MessageRepository.cs#L23).
  - לפני הקריאה לספק, ה-worker נועל את הנמען לדקה, ושומר את הנעילה.
    - בקוד: [DispatchPending.cs:27-29](src/Sms.Core/Application/DispatchPending.cs#L27-L29), שקורא ל-`LockRecipient` ב-[Message.cs:57](src/Sms.Core/Domain/Message.cs#L57). משך הנעילה הוא `LockSeconds` ב-`appsettings.json`.
  - אחרי עלייה מחדש הנעילה פגה, והנמען נשלח שוב עם אותו `reference`, שהוא מזהה הנמען. הספק אמור לזהות שזו חזרה ולא לשלוח שוב.
    - בקוד: נמען שהנעילה שלו פגה נבחר שוב ב-`DueForSending`, ב-[Message.cs:47](src/Sms.Core/Domain/Message.cs#L47). מזהה הנמען נשלח כ-`reference` ב-[DispatchPending.cs:32](src/Sms.Core/Application/DispatchPending.cs#L32), ומגיע לספק ב-[MockSmsProvider.cs:51](src/Sms.Infrastructure/Sending/MockSmsProvider.cs#L51).
  - **פער:** זו הנחה שלא אומתה מול הספק המדומה. אם הספק לא מזהה חזרות, ייתכנו כפילויות במקרה של קריסה.
- **המשתמש לוחץ פעמיים על שליחה:**
  - ב-UI הכפתור מושבת בזמן השליחה, וכל שליחה מקבלת `Idempotency-Key` אחד.
    - בקוד: המפתח נוצר ב-[SendForm.tsx:58](web/src/SendForm.tsx#L58), הכפתור מושבת ב-[SendForm.tsx:111](web/src/SendForm.tsx#L111), והמפתח נשלח בכותרת ב-[api.ts:62](web/src/api.ts#L62).
  - בשרת, `SendMessage` מחזיר את ההודעה המקורית בלי ליצור חדשה. אינדקס ייחודי תופס גם שתי בקשות שמגיעות באותו רגע.
    - בקוד: הבדיקה אם המפתח כבר קיים ב-[SendMessage.cs:11](src/Sms.Core/Application/SendMessage.cs#L11). האינדקס הייחודי ב-[SmsDbContext.cs:28](src/Sms.Infrastructure/Persistence/SmsDbContext.cs#L28). התנגשות באינדקס הופכת לשגיאה ב-[MessageRepository.cs:28](src/Sms.Infrastructure/Persistence/MessageRepository.cs#L28), ונתפסת ב-[SendMessage.cs:21-24](src/Sms.Core/Application/SendMessage.cs#L21-L24), שמחזיר את ההודעה שנשמרה ראשונה.
- **הספק מחזיר הצלחה אבל ההודעה לא הגיעה:**
  - הצלחה מהספק מסמנת את הנמען `Sent`, לא `Delivered`.
    - בקוד: `RecordSendResult` ב-[Message.cs:77](src/Sms.Core/Domain/Message.cs#L77).
  - ה-worker שואל את הספק על הסטטוס. אישור מעביר ל-`Delivered`, ו"לא נמסר" מעביר ל-`Failed`.
    - בקוד: `CheckDeliveryOfSentRecipientsAsync` ב-[DispatchPending.cs:41](src/Sms.Core/Application/DispatchPending.cs#L41). הקריאה לספק ב-[MockSmsProvider.cs:29-38](src/Sms.Infrastructure/Sending/MockSmsProvider.cs#L29-L38). המעבר בין הסטטוסים ב-`RecordDeliveryStatus`, ב-[Message.cs:101-110](src/Sms.Core/Domain/Message.cs#L101-L110).
  - נמען שלא אושר תוך 24 שעות נשאר `Sent`, וזה מצב "לא ידוע" גלוי: אפשר לסנן אותו (`GET /messages?status=Sent`) ולהתריע עליו (סעיף 4).
    - בקוד: חלון 24 השעות ב-[DispatchPending.cs:44](src/Sms.Core/Application/DispatchPending.cs#L44) (`DeliveryTrackingHours`). הסטטוס של ההודעה כולה מחושב מהנמענים ב-`CalculateStatus`, ב-[Message.cs:149](src/Sms.Core/Domain/Message.cs#L149).

</div>
