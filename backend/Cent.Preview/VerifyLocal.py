"""Real HTTP smoke checks. Writes/purchases require explicitly isolated fixture sessions."""
import argparse
import json
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--api', default='http://127.0.0.1:5108/api/cent/v1')
parser.add_argument('--fixtures', type=Path)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
assert urllib.parse.urlparse(args.api).hostname in ('localhost', '127.0.0.1', '::1'), 'Loopback only'
# Local API traffic must bypass an inherited external HTTP proxy.
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
checks = []


def call(path, token=None, body=None, expected=200, method=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    request = urllib.request.Request(args.api.rstrip('/') + path, headers=headers,
        data=None if body is None else json.dumps(body).encode(), method=method)
    try:
        response = opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    data = json.loads(raw) if raw else None
    checks.append({'path': path, 'method': request.get_method(), 'status': response.code,
                   'code': data.get('code') if isinstance(data, dict) else None})
    assert response.code == expected, (path, response.code, data)
    return data


assert call('/status')['payments'] == 'DEVELOPMENT_MOCK'
curriculum = call('/curriculum')
assert len(curriculum['courses']) == 6
assert len(curriculum['topics']) == 247
assert all(isinstance(curriculum[key], list) for key in ('courses', 'chapters', 'topics', 'documents'))
catalog = call('/catalog')
assert len(catalog['subjects']) == 6
assert catalog['currency'] == 'IRR' and catalog['accessMonths'] == 12
assert catalog['basePriceRial'] == 4_900_000
assert all(subject['priceRial'] == 4_900_000 for subject in catalog['subjects'])
assert all(subject['published']['lessons'] > 0 for subject in catalog['subjects'])
assert len(catalog['bundles']) == 1 and len(catalog['bundles'][0]['subjectIds']) == 6
versions = call('/assessment/exam-versions')['versions']
assert len(versions) == 2 and all(not version['availableForNewStarts'] for version in versions)
for version in versions:
    call('/assessment/exam-versions/' + version['examVersionId'] + '/configuration')
google = call('/auth/google/config')
assert google['mode'] == 'GisIdToken'
call('/me/library', expected=401)
call('/me/mastery', expected=401)
call('/auth/login', body={}, expected=404)

if args.fixtures:
    fixture_path = args.fixtures.resolve()
    preview = Path(__file__).resolve().parent
    assert fixture_path.is_relative_to(preview / 'App_Data' / 'isolated'), 'Disposable fixtures only'
    fixtures = json.loads(fixture_path.read_text(encoding='utf-8-sig'))
    learner = next(f for f in fixtures if f['role'] == 'STUDENT')['token']
    other = next(f for f in fixtures if f['role'] == 'ContentManager')['token']
    assert all(subject['available'] for subject in catalog['subjects']), 'Use explicit sandbox offers'
    call('/me', learner)
    subject_ids = [s['subjectId'] for s in catalog['subjects']]
    prices = []
    for count in range(1, 7):
        quote = call('/subject-quotes', learner, {'subjectIds': subject_ids[:count]})
        percent = (count - 1) * 5
        assert quote['distinctCount'] == count and quote['discountPercent'] == percent
        assert quote['payableRial'] == count * 4_900_000 * (100 - percent) // 100
        assert sum(line['payableRial'] for line in quote['lines']) == quote['payableRial']
        prices.append({'subjects': count, 'discountPercent': percent, 'payableRial': quote['payableRial']})
    call('/subject-quotes', learner, {'subjectIds': ['math', 'math']}, expected=400)
    quote = call('/subject-quotes', learner, {'subjectIds': ['math', 'reading']})
    call('/subject-quotes/' + quote['id'], learner)
    call('/subject-quotes/' + quote['id'], other, expected=404)
    request_id = str(uuid.uuid4())
    order = call('/subject-checkout', learner, {'quoteId': quote['id'], 'requestId': request_id})
    repeated = call('/subject-checkout', learner, {'quoteId': quote['id'], 'requestId': request_id})
    assert order['id'] == repeated['id']
    call('/development/payments/' + order['id'], learner, {'outcome': 'success'})
    for _ in range(2):
        assert call('/me/orders/' + order['id'] + '/verify', learner, {})['paid']
    detail = call('/me/orders/' + order['id'], learner)
    assert detail['order']['status'] == 'PAID' and len(detail['access']) == 2
    call('/me/orders/' + order['id'], other, expected=404)
    assert len(call('/me/subject-access', learner)) == 2
    library = call('/me/library', learner)
    reading = next(s for s in catalog['subjects'] if s['subjectId'] == 'reading')
    document = next(d for d in curriculum['documents'] if d['trainingId'] == reading['trainingId'])
    sections = call('/documents/' + document['id'] + '/source-sections', learner)
    assert sections and any(len(s['text']) > 100 for s in sections)
    assert document['id'] in library['readableDocumentIds']
    call('/documents/' + document['id'], learner)
    call('/documents/' + document['id'] + '/source-sections', other, expected=403)
    call('/documents/' + document['id'] + '/progress', learner, {'page': 1}, method='PUT')
    note_id = str(uuid.uuid4())
    note = call('/notes/' + note_id, learner,
        {'documentId': document['id'], 'page': 1, 'anchorY': 0.25, 'body': 'Disposable local regression note', 'revision': 0}, method='PUT')
    assert note['body'] == 'Disposable local regression note'
    call('/me/notes', learner)
    attempt = call('/attempts/', learner, {'kind': 'practice', 'requestId': str(uuid.uuid4()), 'course': 'reading', 'contentContract': 'passage-v1'})
    assert attempt['questions'] and all('correctAnswer' not in q for q in attempt['questions'])
    assert attempt['contentContract'] == 'passage-v1' and attempt['passages']
    result = call('/attempts/' + attempt['id'] + '/submit', learner, {'answers': {q['id']: 0 for q in attempt['questions']}})
    assert result['review'] and result['result']
    call('/attempts/' + attempt['id'], learner)
    call('/attempts/', learner)
    call('/me/mastery?syllabusRevisionId=' + urllib.parse.quote(curriculum['syllabusRevisionId']), learner)
    call('/assessment/mock-attempts/?limit=20', learner)
    renewal = call('/subject-quotes', learner, {'subjectIds': ['reading']})
    assert renewal['lines'][0]['currentAccessEnd']
    call('/subject-checkout', learner, {'quoteId': renewal['id'], 'requestId': str(uuid.uuid4())}, expected=409)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    (args.output.parent / 'verified-pricing.json').write_text(json.dumps(prices, indent=2))
else:
    assert all(not subject['available'] for subject in catalog['subjects']), 'Existing preview keeps review gates'

report = {'api': args.api, 'checks': checks, 'checkCount': len(checks),
          'courses': len(curriculum['courses']), 'topics': len(curriculum['topics']),
          'documents': len(curriculum['documents']), 'subjects': len(catalog['subjects']),
          'googleEnabled': google['enabled'], 'sandbox': bool(args.fixtures)}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in report.items() if key != 'checks'}))
