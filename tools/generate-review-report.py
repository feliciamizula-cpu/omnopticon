#!/usr/bin/env python3
import json, os, re, html, uuid
from datetime import datetime

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REVIEWS_DIR = os.path.join(SCRIPT_DIR, "reviews")
OUTPUT_FILE = os.path.join(SCRIPT_DIR, "reviews", "code-review-report.html")
WORK_DIR = os.path.normpath(os.path.join(SCRIPT_DIR, ".."))

review_files = sorted([
    f for f in os.listdir(REVIEWS_DIR)
    if f.endswith(".md") and f not in ("REVIEW_LOG.md", "code-review-report.html")
])

reviews = []
for rf in review_files:
    path = os.path.join(REVIEWS_DIR, rf)
    with open(path) as f:
        content = f.read()

    reviewer = "unknown"
    m = re.search(r'Reviewer:\s*(\S+)', content)
    if m:
        reviewer = m.group(1)

    batch = ""
    m = re.search(r'Batch:\s*(\S+)', content)
    if m:
        batch = m.group(1)

    status = "unknown"
    m = re.search(r'Status:\s*(\S+)', content)
    if m:
        status = m.group(1)

    started = ""
    m = re.search(r'Started:\s*(\S+)', content)
    if m:
        started = m.group(1)

    findings_section = ""
    m = re.search(r'## Findings\n(.*?)(?=\n## |\Z)', content, re.DOTALL)
    if m:
        findings_section = m.group(1).strip()

    sev_markers = {
        'critical': re.findall(r'^###\s+(?:CRITICAL|Critical)\b', findings_section, re.MULTILINE),
        'high': re.findall(r'^###\s+(?:HIGH|High)\b(?!\s+Priority)', findings_section, re.MULTILINE),
        'medium': re.findall(r'^###\s+(?:MEDIUM|Medium)\b', findings_section, re.MULTILINE),
        'low': re.findall(r'^###\s+(?:LOW|Low)\b', findings_section, re.MULTILINE),
    }

    severity_counts = {k: len(v) for k, v in sev_markers.items()}

    has_findings = bool(findings_section) and "_Reviewer output pending._" not in findings_section

    reviews.append({
        'file': rf,
        'reviewer': reviewer,
        'batch': batch,
        'status': status,
        'started': started,
        'content': content,
        'severity_counts': severity_counts,
        'has_findings': has_findings,
    })

total_critical = sum(r['severity_counts']['critical'] for r in reviews)
total_high = sum(r['severity_counts']['high'] for r in reviews)
total_medium = sum(r['severity_counts']['medium'] for r in reviews)
total_low = sum(r['severity_counts']['low'] for r in reviews)
total_findings = total_critical + total_high + total_medium + total_low
now = datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S UTC')


def md_to_html(text):
    text = html.escape(text)
    text = re.sub(r'```(\w*)\n(.*?)```', r'<pre><code>\2</code></pre>', text, flags=re.DOTALL)
    text = re.sub(r'`([^`]+)`', r'<code>\1</code>', text)
    text = re.sub(r'\*\*(.*?)\*\*', r'<strong>\1</strong>', text)
    lines = text.split('\n')
    result = []
    in_list = False
    for line in lines:
        if re.match(r'^\s*(\d+\.|\-|\*)\s', line):
            if not in_list:
                result.append('<ul>')
                in_list = True
            result.append('<li>' + re.sub(r'^\s*(\d+\.|\-|\*)\s', '', line) + '</li>')
        else:
            if in_list:
                result.append('</ul>')
                in_list = False
            if line.strip():
                result.append('<p>' + line + '</p>')
    if in_list:
        result.append('</ul>')
    return '\n'.join(result)


def extract_severity_blocks(content):
    blocks = []
    lines = content.split('\n')
    current_sev = None
    current_lines = []
    in_findings = False

    for line in lines:
        s = line.strip()
        if s == '## Findings':
            in_findings = True
            continue
        if in_findings and line.startswith('## '):
            break
        if in_findings:
            sev_match = re.match(r'^###\s+(CRITICAL|HIGH|MEDIUM|LOW|Critical|High|Medium|Low)', s)
            if sev_match:
                if current_sev:
                    blocks.append({'severity': current_sev, 'content': '\n'.join(current_lines).strip()})
                current_sev = sev_match.group(1).lower()
                current_lines = [line]
            elif current_sev:
                current_lines.append(line)
    if current_sev:
        blocks.append({'severity': current_sev, 'content': '\n'.join(current_lines).strip()})
    return blocks


# Build HTML
H = []

H.append('''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Argus Recon - Code Review Report</title>
<style>
:root{--bg:#0d1117;--surface:#161b22;--border:#30363d;--text:#c9d1d9;--text-muted:#8b949e;--critical:#f85149;--high:#d29922;--medium:#58a6ff;--low:#8b949e;--accent:#58a6ff}
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;background:var(--bg);color:var(--text);line-height:1.6;padding:0}
.container{max-width:1100px;margin:0 auto;padding:24px}
.header{background:linear-gradient(135deg,#161b22,#0d1117);border-bottom:1px solid var(--border);padding:32px 0;margin-bottom:24px}
.header h1{font-size:28px;font-weight:600;color:#f0f6fc}
.header .subtitle{color:var(--text-muted);font-size:14px;margin-top:4px}
.header .meta{display:flex;gap:24px;margin-top:12px;font-size:13px;color:var(--text-muted)}
.summary-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin-bottom:24px}
.summary-card{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:16px}
.summary-card .label{font-size:12px;text-transform:uppercase;letter-spacing:.5px;color:var(--text-muted)}
.summary-card .value{font-size:32px;font-weight:700;margin-top:4px}
.summary-card .value.critical{color:var(--critical)}
.summary-card .value.high{color:var(--high)}
.summary-card .value.medium{color:var(--medium)}
.summary-card .value.low{color:var(--low)}
.summary-card .value.total{color:var(--text)}
.review-table{width:100%;border-collapse:collapse;margin-bottom:24px;background:var(--surface);border-radius:8px;overflow:hidden;border:1px solid var(--border)}
.review-table th{background:#1c2128;padding:10px 14px;text-align:left;font-size:12px;text-transform:uppercase;letter-spacing:.5px;color:var(--text-muted);border-bottom:1px solid var(--border)}
.review-table td{padding:10px 14px;border-bottom:1px solid var(--border);font-size:13px}
.review-table tr:last-child td{border-bottom:none}
.severity-badge{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600;margin-right:4px}
.severity-badge.critical{background:rgba(248,81,73,0.15);color:var(--critical)}
.severity-badge.high{background:rgba(210,153,34,0.15);color:var(--high)}
.severity-badge.medium{background:rgba(88,166,255,0.15);color:var(--medium)}
.severity-badge.low{background:rgba(139,148,158,0.15);color:var(--low)}
.status-badge{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600}
.status-badge.complete{background:rgba(63,185,80,0.15);color:#3fb950}
.status-badge.in_progress{background:rgba(210,153,34,0.15);color:var(--high)}
.finding{background:var(--surface);border:1px solid var(--border);border-radius:8px;margin-bottom:12px;overflow:hidden}
.finding-header{padding:12px 16px;cursor:pointer;display:flex;justify-content:space-between;align-items:center}
.finding-header:hover{background:#1c2128}
.finding-meta{display:flex;align-items:center;gap:8px}
.finding-reviewer{font-size:12px;color:var(--text-muted)}
.finding-body{padding:16px;border-top:1px solid var(--border);display:none}
.finding-body.open{display:block}
.finding-body pre{background:#0d1117;border:1px solid var(--border);border-radius:6px;padding:12px;overflow-x:auto;font-family:SF Mono,Fira Code,monospace;font-size:13px;line-height:1.4;margin:8px 0}
.finding-body code{background:#0d1117;padding:2px 4px;border-radius:3px;font-family:SF Mono,monospace;font-size:13px}
.finding-body h4{color:var(--text);margin:12px 0 4px;font-size:14px}
.finding-body ul{margin:4px 0 8px;padding-left:20px}
.finding-body li{margin:4px 0}
.toggle-icon{font-size:14px;color:var(--text-muted)}
.finding-section-title{font-size:18px;font-weight:600;margin:24px 0 12px;padding-bottom:8px;border-bottom:1px solid var(--border)}
.question-item{background:var(--surface);border:1px solid var(--border);border-radius:8px;margin-bottom:8px}
.question-header{padding:12px 16px;cursor:pointer;display:flex;justify-content:space-between;align-items:center;font-size:14px}
.question-header:hover{background:#1c2128}
.question-body{padding:0 16px 12px;border-top:1px solid var(--border);display:none;font-size:13px;color:var(--text-muted)}
.question-body.open{display:block;padding-top:12px}
nav{position:sticky;top:0;background:var(--bg);border-bottom:1px solid var(--border);padding:8px 0;z-index:10}
nav a{color:var(--accent);text-decoration:none;font-size:13px;margin-right:16px}
nav a:hover{text-decoration:underline}
</style>
</head>
<body>
<div class="header"><div class="container">
<h1>Argus Recon &mdash; Code Review Report</h1>
<div class="subtitle">Aggregated findings from all reviewer agents</div>
<div class="meta"><span>Generated: ''' + now + '''</span><span>Reviews: ''' + str(len(reviews)) + '''</span><span>Total Findings: ''' + str(total_findings) + '''</span></div>
</div></div>
<nav><div class="container">
<a href="#summary">Summary</a>
<a href="#reviews">Reviews</a>
<a href="#critical-findings">Critical Findings</a>
<a href="#all-findings">All Findings</a>
<a href="#questions">Open Questions</a>
</div></nav>
<div class="container">

<section id="summary">
<h2 class="finding-section-title">Summary</h2>
<div class="summary-grid">
<div class="summary-card"><div class="label">Total Findings</div><div class="value total">''' + str(total_findings) + '''</div></div>
<div class="summary-card"><div class="label">Critical</div><div class="value critical">''' + str(total_critical) + '''</div></div>
<div class="summary-card"><div class="label">High</div><div class="value high">''' + str(total_high) + '''</div></div>
<div class="summary-card"><div class="label">Medium</div><div class="value medium">''' + str(total_medium) + '''</div></div>
<div class="summary-card"><div class="label">Low</div><div class="value low">''' + str(total_low) + '''</div></div>
<div class="summary-card"><div class="label">Review Documents</div><div class="value total">''' + str(len(reviews)) + '''</div></div>
</div>
</section>

<section id="reviews">
<h2 class="finding-section-title">Review Documents</h2>
<table class="review-table">
<tr><th>File</th><th>Reviewer</th><th>Status</th><th>Findings</th><th>Batch</th></tr>
''')

for r in reviews:
    sev_badges = ''
    for sev in ['critical', 'high', 'medium', 'low']:
        c = r['severity_counts'][sev]
        if c > 0:
            sev_badges += '<span class="severity-badge ' + sev + '">' + str(c) + ' ' + sev + '</span>'
    sc = 'complete' if r['status'] == 'complete' else 'in_progress'
    sd = 'Complete' if r['status'] == 'complete' else 'In Progress'
    fn = r['file'][:37] + '...' if len(r['file']) > 40 else r['file']
    H.append('<tr><td><a href="#review-' + html.escape(r['reviewer'] + '-' + r['batch'][:8], quote=True) + '" style="color:var(--accent);text-decoration:none;">' + html.escape(fn) + '</a></td>')
    H.append('<td>' + html.escape(r['reviewer']) + '</td>')
    H.append('<td><span class="status-badge ' + sc + '">' + sd + '</span></td>')
    H.append('<td>' + sev_badges + '</td>')
    H.append('<td><code style="font-size:11px;color:var(--text-muted);">' + html.escape(r['batch'][:12]) + '</code></td></tr>')

H.append('</table></section>')

# Open Questions
H.append('<section id="questions"><h2 class="finding-section-title">Open Questions</h2>')
for r in reviews:
    m = re.search(r'## Open Questions\n(.*?)(?=\n## |\Z)', r['content'], re.DOTALL)
    if m:
        qt = m.group(1).strip()
        if qt and '_Reviewer output pending._' not in qt:
            questions = re.split(r'\n\s*(?=\d+\.)', qt)
            for q in questions:
                q = q.strip()
                if not q:
                    continue
                qlines = q.split('\n')
                qh = qlines[0]
                qb = '\n'.join(qlines[1:]) if len(qlines) > 1 else ''
                H.append('<div class="question-item"><div class="question-header" onclick="this.nextElementSibling.classList.toggle(\'open\')"><span>' + html.escape(qh[:120]) + '</span><span class="toggle-icon">&#9660;</span></div>')
                H.append('<div class="question-body">' + (md_to_html(qb) if qb else 'No additional details.') + '</div></div>')

H.append('</section>')

# Follow-up actions
follow_ups = []
for r in reviews:
    m = re.search(r'## Follow-Up\n(.*?)(?=\n## |\Z)', r['content'], re.DOTALL)
    if m:
        ft = m.group(1).strip()
        if ft and '_Reviewer output pending._' not in ft:
            items = re.findall(r'(?:^|\n)\s*[\-\*]\s*(.*)', ft)
            for item in items:
                item = item.strip()
                if item and item not in follow_ups:
                    follow_ups.append(item)

H.append('<section><h2 class="finding-section-title">Follow-Up Actions</h2>')
H.append('<div class="summary-grid"><div class="summary-card"><div class="label">Total Actions</div><div class="value total">' + str(len(follow_ups)) + '</div></div></div>')
H.append('<ul style="list-style:none;padding:0;">')
for fu in follow_ups:
    H.append('<li style="padding:8px 12px;background:var(--surface);border:1px solid var(--border);border-radius:6px;margin-bottom:6px;font-size:14px;">' + md_to_html(fu) + '</li>')
H.append('</ul></section>')

# Build finding index
finding_index = []
for r in reviews:
    blocks = extract_severity_blocks(r['content'])
    for block in blocks:
        fid = str(uuid.uuid4())[:8]
        bc = block['content']
        tm = re.search(r'^###\s+\S+\s+(.*)', bc, re.MULTILINE)
        title = tm.group(1).strip() if tm else bc[:80].strip()
        finding_index.append({
            'id': fid, 'severity': block['severity'], 'reviewer': r['reviewer'],
            'batch': r['batch'][:12], 'title': title, 'content': bc, 'file': r['file']
        })

# Findings sections
for sev in ['critical', 'high', 'medium', 'low']:
    findings = [f for f in finding_index if f['severity'] == sev]
    if not findings:
        continue

    section_id = 'critical-findings' if sev == 'critical' else None

    if sev == 'critical':
        H.append('<section id="critical-findings"><h2 class="finding-section-title" style="color:var(--critical)">Critical Findings</h2>')
    else:
        H.append('<section><h3 style="margin-top:24px;margin-bottom:12px;font-size:16px;">' + sev.capitalize() + '</h3>')

    for f in findings:
        body = md_to_html(f['content'])
        sev_color = {'critical': 'var(--critical)', 'high': 'var(--high)', 'medium': 'var(--medium)', 'low': 'var(--low)'}
        H.append('<div class="finding"><div class="finding-header" onclick="this.nextElementSibling.classList.toggle(\'open\')">')
        H.append('<div class="finding-meta"><span class="severity-badge ' + html.escape(f['severity']) + '">' + html.escape(f['severity']) + '</span>')
        H.append('<span style="font-weight:600;">' + html.escape(f['title'][:100]) + '</span>')
        H.append('<span class="finding-reviewer">' + html.escape(f['reviewer']) + '</span></div>')
        H.append('<span class="toggle-icon">&#9660;</span></div>')
        H.append('<div class="finding-body" id="finding-' + html.escape(f['id']) + '">' + body + '</div></div>')

    H.append('</section>')

H.append('''
</div>
<script>
if(window.location.hash){var t=document.querySelector(window.location.hash);if(t)t.classList.add("open")}
</script>
</body>
</html>''')

with open(OUTPUT_FILE, 'w') as f:
    f.write('\n'.join(H))

print(f"Report written to {OUTPUT_FILE}")
print(f"Total reviews: {len(reviews)}")
print(f"Total findings: {total_findings} ({total_critical}C, {total_high}H, {total_medium}M, {total_low}L)")
