namespace RevitModelMcp.Core.Batch;

public sealed record NativeDialogControl(string ClassName, string? Text, int ControlId, bool IsPushButton);

public static class BatchDialogText
{
    public const int MaximumMessageLength = 2000;
    public const int MaximumButtons = 10;

    public static BatchDialogRecord FromFields(IReadOnlyDictionary<string, object> fields)
    {
        if (fields is null) throw new ArgumentNullException(nameof(fields));

        var buttons = new List<BatchDialogButton>();
        if (fields.TryGetValue("buttons", out var buttonValues) && buttonValues is System.Collections.IEnumerable entries)
        {
            foreach (var entry in entries)
            {
                if (buttons.Count >= MaximumButtons) break;
                if (entry is not Dictionary<string, object> buttonFields ||
                    !buttonFields.TryGetValue("caption", out var caption) ||
                    !buttonFields.TryGetValue("result", out var buttonResult)) continue;
                buttons.Add(new BatchDialogButton
                {
                    Caption = Convert.ToString(caption)!,
                    Result = Convert.ToInt32(buttonResult)
                });
            }
        }

        return new BatchDialogRecord
        {
            DialogId = Convert.ToString(fields["dialogId"])!,
            Type = Convert.ToString(fields["type"])!,
            Message = fields.TryGetValue("message", out var message) && message is not null ? Convert.ToString(message) : null,
            Buttons = buttons.Count > 0 ? buttons : null,
            Decision = Convert.ToString(fields["decision"])!,
            Result = fields.TryGetValue("result", out var result) && result is not null ? Convert.ToInt32(result) : null,
            ModelPath = Convert.ToString(fields["modelPath"])!,
            Phase = Convert.ToString(fields["phase"])!,
            TimeUtc = Convert.ToString(fields["timeUtc"])!
        };
    }

    public static string? Cap(string? text)
    {
        if (text is null || string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length <= MaximumMessageLength) return normalized;
        var length = MaximumMessageLength;
        if (char.IsHighSurrogate(normalized[length - 1]) && char.IsLowSurrogate(normalized[length])) length--;
        return normalized[..length];
    }

    public static string? JoinMessage(IEnumerable<string?> parts)
    {
        if (parts is null) throw new ArgumentNullException(nameof(parts));
        var lines = new List<string>();
        foreach (var part in parts)
        {
            var normalized = Cap(part);
            if (normalized is null || (lines.Count > 0 && lines[^1] == normalized)) continue;
            lines.Add(normalized);
        }
        return Cap(string.Join("\n", lines));
    }

    public static (string? Message, IReadOnlyList<BatchDialogButton> Buttons) Parse(IEnumerable<NativeDialogControl> controls)
    {
        if (controls is null) throw new ArgumentNullException(nameof(controls));
        var messageParts = new List<string?>();
        var buttons = new List<BatchDialogButton>();
        foreach (var control in controls)
        {
            if (control.ClassName.Equals("Static", StringComparison.OrdinalIgnoreCase) ||
                control.ClassName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                control.ClassName.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
            {
                messageParts.Add(control.Text);
            }
            else if (control.ClassName.Equals("Button", StringComparison.OrdinalIgnoreCase) &&
                     control.IsPushButton && buttons.Count < MaximumButtons)
            {
                var caption = StripAccelerators(control.Text);
                if (caption is null || buttons.Any(button => button.Result == control.ControlId && button.Caption == caption)) continue;
                buttons.Add(new BatchDialogButton { Caption = caption, Result = control.ControlId });
            }
        }
        return (JoinMessage(messageParts), buttons);
    }

    public static string DescribeUnknown(BatchDialogRecord record)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        var description = $"Unknown dialog '{record.DialogId}': {record.Message ?? "No message available."}";
        if (record.Buttons is { Count: > 0 } buttons)
            description += $" Buttons: {string.Join(", ", buttons.Select(button => $"{button.Result}={button.Caption}"))}.";
        return description;
    }

    private static string? StripAccelerators(string? text)
    {
        if (text is null || string.IsNullOrWhiteSpace(text)) return null;
        var caption = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '&' && index + 1 < text.Length && text[index + 1] == '&')
            {
                caption.Append('&');
                index++;
            }
            else if (text[index] != '&') caption.Append(text[index]);
        }
        return Cap(caption.ToString());
    }
}
