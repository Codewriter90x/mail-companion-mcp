import AppKit
import EventKit
import Foundation

// A bundled, single-request helper: never receives mailbox credentials or mail bodies.
// LaunchServices provides a stable application identity for macOS privacy consent.
struct Request: Decodable {
    let action: String
    var calendar_id: String?
    var previous_calendar_id: String?
    var reminder_id: String?
    var followup_id: String?
    var title: String?
    var notes: String?
    var due: String?
    var closed: Bool?
    var rearm: Bool?
}
struct HelperError: Error { let message: String }
let args = CommandLine.arguments
func argument(_ name: String) -> String? {
    guard let index = args.firstIndex(of: name), args.count > index + 1 else { return nil }
    return args[index + 1]
}
if args.contains("--capabilities") {
    print("Mail Companion Apple Reminders helper; macOS 14+; EventKit; protocol 1")
    exit(0)
}
guard let requestPath = argument("--request"), let responsePath = argument("--response") else { exit(2) }
let store = EKEventStore()
func finish(_ value: [String: Any]) -> Never {
    do {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        // Parent creates both files with mode 600 in a private temporary directory.
        try data.write(to: URL(fileURLWithPath: responsePath))
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: responsePath)
    } catch { exit(2) }
    exit(0)
}
func calendars() -> [EKCalendar] { store.calendars(for: .reminder).filter { $0.allowsContentModifications } }
func listResult() -> [String: Any] {
    ["success": true, "calendars": calendars().map { ["id": $0.calendarIdentifier, "title": $0.title, "source": $0.source.title] }]
}
func fetch(_ lists: [EKCalendar]) -> [EKReminder] {
    var result: [EKReminder]? = nil
    let predicate = store.predicateForReminders(in: lists)
    store.fetchReminders(matching: predicate) { reminders in
        DispatchQueue.main.async { result = reminders ?? [] }
    }
    let deadline = Date().addingTimeInterval(15)
    while result == nil && Date() < deadline { RunLoop.current.run(until: Date().addingTimeInterval(0.05)) }
    guard let result else { finish(["error": "Lettura Promemoria non completata. Riprova."]) }
    return result
}
func perform(_ request: Request) throws -> [String: Any] {
    if request.action == "status" {
        return ["success": true, "authorized": EKEventStore.authorizationStatus(for: .reminder) == .fullAccess]
    }
    if request.action == "authorize" {
        var granted: Bool? = nil
        store.requestFullAccessToReminders { allowed, _ in
            DispatchQueue.main.async { granted = allowed }
        }
        let deadline = Date().addingTimeInterval(100)
        while granted == nil && Date() < deadline { RunLoop.current.run(until: Date().addingTimeInterval(0.05)) }
        guard granted == true else { throw HelperError(message: "Accesso a Promemoria non concesso. Abilitalo nelle impostazioni Privacy e sicurezza del Mac.") }
        return listResult()
    }
    guard EKEventStore.authorizationStatus(for: .reminder) == .fullAccess else {
        throw HelperError(message: "Autorizza prima l’accesso a Promemoria dal pannello locale.")
    }
    if request.action == "lists" { return listResult() }
    guard request.action == "upsert", let calendarID = request.calendar_id,
          let calendar = calendars().first(where: { $0.calendarIdentifier == calendarID }),
          let key = request.followup_id, UUID(uuidString: key) != nil,
          let title = request.title, !title.isEmpty, title.count <= 220,
          let notes = request.notes, notes.count <= 2500,
          let dueText = request.due, let due = ISO8601DateFormatter().date(from: dueText) else {
        throw HelperError(message: "Lista non disponibile o dati del promemoria non validi.")
    }
    let url = URL(string: "mailcompanion://followup/" + key)!
    var existing: EKReminder? = nil
    if let identifier = request.reminder_id,
       let item = store.calendarItem(withIdentifier: identifier) as? EKReminder, item.url == url { existing = item }
    if existing == nil {
        var lists = [calendar]
        if let previousID = request.previous_calendar_id, previousID != calendarID,
           let previous = calendars().first(where: { $0.calendarIdentifier == previousID }) { lists.append(previous) }
        let matches = fetch(lists).filter { $0.url == url }
        // Never guess when an external edit or manual copy has duplicated our key.
        if matches.count > 1 { throw HelperError(message: "Promemoria duplicati per questa questione: controlla la lista prima di riprovare.") }
        existing = matches.first
    }
    if request.closed == true && existing == nil { return ["success": true, "reminder_id": "", "state": "absent"] }
    let reminder = existing ?? EKReminder(eventStore: store)
    reminder.calendar = calendar
    reminder.title = title
    reminder.notes = notes
    reminder.url = url
    var components = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute, .second], from: due)
    components.timeZone = TimeZone.current
    reminder.dueDateComponents = components
    if request.closed == true {
        reminder.isCompleted = true
        reminder.alarms = []
    } else if existing == nil || request.rearm == true {
        reminder.isCompleted = false
        // A past review date is delivered once shortly after sync, not on every poll.
        reminder.alarms = [EKAlarm(absoluteDate: max(due, Date().addingTimeInterval(5)))]
    }
    guard FileManager.default.fileExists(atPath: requestPath) else {
        throw HelperError(message: "Richiesta annullata.")
    }
    try store.save(reminder, commit: true)
    return ["success": true, "reminder_id": reminder.calendarItemIdentifier, "state": reminder.isCompleted ? "completed" : "scheduled"]
}
do {
    let data = try Data(contentsOf: URL(fileURLWithPath: requestPath))
    guard data.count <= 16384 else { throw HelperError(message: "Richiesta troppo grande.") }
    let request = try JSONDecoder().decode(Request.self, from: data)
    finish(try perform(request))
} catch let error as HelperError { finish(["error": error.message]) }
catch { finish(["error": "Operazione Promemoria non riuscita. Verifica autorizzazione e lista selezionata."]) }
