(() => {
"use strict";
const KEY="biima-v3-complete", $=id=>document.getElementById(id);
const esc=v=>String(v==null?"":v).replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const states=["VIC","NSW","QLD","SA","WA","TAS","ACT","NT"];
const stages=["Safety & emergency","Accident details","Driver & vehicle","Photos & notes","Review","Submit"];
const blank=()=>({step:0,id:"DRAFT-"+Date.now().toString(36),safe:"",injuries:"",injuryNotes:"",police:false,fire:false,ambulance:false,none:false,type:"",location:"",when:"",involved:"",people:"",name:"",mobile:"",rego:"",state:"VIC",fleet:"",source:"Manual",confirmed:false,photos:[],audio:"",language:"Punjabi",notes:"",consent:false});
const initial=()=>({draft:blank(),reports:[],fleets:[{id:"f1",name:"ABC Transport",contact:"John Smith",active:true},{id:"f2",name:"XYZ Logistics",contact:"Sarah Kim",active:true}],vehicles:[{id:"v1",rego:"ABC123",fleet:"f1",type:"Truck",active:true},{id:"v2",rego:"ABC456",fleet:"f1",type:"Truck",active:true},{id:"v3",rego:"XYZ458",fleet:"f2",type:"Van",active:true}],drivers:[{id:"d1",name:"John Smith",mobile:"0412 345 678",fleet:"f1",vehicle:"v1",active:true},{id:"d2",name:"Aman Singh",mobile:"0415 333 444",fleet:"f1",vehicle:"v2",active:true},{id:"d3",name:"Sarah Kim",mobile:"0413 200 100",fleet:"f2",vehicle:"v3",active:true}],sms:[]});
let db;try{db=JSON.parse(localStorage.getItem(KEY))||initial()}catch{db=initial()}
for(const k of ["reports","fleets","vehicles","drivers","sms"])if(!Array.isArray(db[k]))db[k]=initial()[k];if(!db.draft)db.draft=blank();if(!Array.isArray(db.draft.photos))db.draft.photos=[];
if(db.flowVersion!==2){const oldStep=Math.min(7,Math.max(0,Number(db.draft.step)||0));db.draft.step=[0,0,0,1,2,3,4,5][oldStep];db.flowVersion=2;try{localStorage.setItem(KEY,JSON.stringify(db))}catch{}}
let section=db.draft.step>0?"resume":"driver",auth=false,tab="dashboard",selected="",editId="",method="manual",fleetId="f1",type="Truck",assignFleet="",assignVehicle="",driverGroup="single",message="",recorder=null,stream=null,mediaURLs={},dark=localStorage.getItem(KEY+"-dark")==="yes";
document.documentElement.classList.toggle("dark",dark);
const d=()=>db.draft, save=()=>{try{localStorage.setItem(KEY,JSON.stringify(db))}catch{message="Unable to save browser draft. Storage may be full or disabled."}};
const fleet=id=>db.fleets.find(x=>x.id===id), veh=id=>db.vehicles.find(x=>x.id===id), drv=id=>db.drivers.find(x=>x.id===id);
const row=(k,v)=>'<div class="details"><span>'+esc(k)+'</span><b>'+esc(v||"Not provided")+'</b></div>';
const btn=(text,act,cls="")=>'<button class="btn '+cls+'" data-act="'+act+'">'+esc(text)+'</button>';
const box=(text,on,act)=>'<button class="choice '+(on?"active":"")+'" data-act="'+act+'">'+esc(text)+'</button>';
const cards=(choices,cols=2)=>'<div class="cards '+(cols===3?"three":cols===4?"four":"")+'">'+choices.join("")+'</div>';
const opts=(k,values,cols=2)=>cards(values.map(v=>box(v,d()[k]===v,"set:"+k+":"+v)),cols);
const f=(key,label,placeholder="",long=false)=>'<div class="field"><label>'+esc(label)+'</label>'+(long?'<textarea data-input="'+key+'" placeholder="'+esc(placeholder)+'">'+esc(d()[key])+'</textarea>':'<input data-input="'+key+'" value="'+esc(d()[key])+'" placeholder="'+esc(placeholder)+'">')+'</div>';
const inf=(name,label,value)=>'<div class="field"><label>'+esc(label)+'</label><input data-form="'+name+'" value="'+esc(value||"")+'"></div>';
const formVal=name=>document.querySelector('[data-form="'+name+'"]')?.value.trim()||"";
const emergency=x=>[x.police&&"Police",x.fire&&"Fire brigade",x.ambulance&&"Ambulance"].filter(Boolean).join(", ")||(x.none?"None required":"Not specified");
const detail=x=>'<h2 class="subtitle">Safety and emergency</h2>'+row("Safe?",x.safe)+row("Injuries",x.injuries)+row("Injury notes",x.injuryNotes)+row("Emergency services",emergency(x))+'<h2 class="subtitle">Accident and parties</h2>'+row("Incident",x.type)+row("Location",x.location)+row("Date/time",x.when)+row("Others involved",x.involved)+row("Other drivers/witnesses",x.people)+'<h2 class="subtitle">Driver and vehicle</h2>'+row("Driver",x.name)+row("Mobile",x.mobile)+row("Registration",x.rego)+row("State",x.state)+row("Fleet",x.fleet||"Unmatched")+row("Identification",x.source)+'<h2 class="subtitle">Evidence</h2>'+row("Photos",x.photos?.length||0)+row("Voice note",x.audio?"Attached":"Not attached")+row("Language",x.language)+row("Notes",x.notes);
const nav=()=>'<div class="nav">'+(d().step?btn("← Back","back"):"")+(d().step<5?btn("Continue →","next","primary grow"):"")+'</div>';
const progress=()=>'<div class="row between"><b>'+stages[d().step]+'</b><span class="small muted">'+(d().step+1)+' / 6</span></div><div class="progress">'+stages.map((_,i)=>'<i class="'+(i<=d().step?"on":"")+'"></i>').join("")+'</div><div class="status">✓ Saved on this device · Server sync is not yet implemented</div>';
function driver(){
if(section==="resume")return '<div class="card driver"><div class="eyebrow">UNFINISHED REPORT FOUND</div><h1 class="title">Welcome back</h1><p class="muted">An unfinished report has been saved locally on this device.</p><div class="panel"><b>'+esc(d().type||"Unfinished incident")+'</b><p class="small muted">'+esc(d().location||"Location not entered")+'</p><p>Draft reference: '+esc(d().id)+'</p><b>Step '+(d().step+1)+' of 6 · '+stages[d().step]+'</b><div class="progress">'+stages.map((_,i)=>'<i class="'+(i<=d().step?"on":"")+'"></i>').join("")+'</div><p class="status">✓ Browser draft restored</p></div>'+btn("Resume report →","resume","primary block")+'<p>'+btn("Review saved details","saved","block")+'</p>'+btn("Start new report","new","block")+'</div>';
if(section==="saved")return '<div class="card driver"><h1 class="title">Saved draft details</h1>'+detail(d())+btn("Continue editing →","resume","primary block")+'</div>';
if(section==="done")return '<div class="card driver"><h1 class="title">✓ Demo report complete</h1><p class="muted">The report is visible in the simulated broker queue. Nothing was sent to BIIMA.</p>'+btn("Open broker portal","broker","primary block")+'<p>'+btn("Start new report","new","block")+'</p></div>';
let html='<div class="card driver">'+progress();
switch(d().step){
case 0:
html+='<div class="eyebrow">SAFETY FIRST</div><h1 class="title">Are you safe right now?</h1><p class="muted">Move somewhere safe before continuing.</p>'+opts("safe",["I'm safe","I need help"] )+'<div class="alert">If anyone is in immediate danger or seriously injured, call emergency services immediately.</div><p><a href="tel:000" class="btn danger block" style="display:block;text-align:center;text-decoration:none">☎ Dial 000</a></p><a href="tel:+61384013318">Dial BIIMA</a>';
html+='<hr class="rule">';
html+='<div class="eyebrow">INJURIES</div><h2 class="subtitle">Is anyone injured?</h2>'+opts("injuries",["No injuries","Yes, injuries","Unsure"])+f("injuryNotes","Injury notes","Details, if known",true);
html+='<hr class="rule">';
html+='<div class="eyebrow">EMERGENCY RESPONSE</div><h2 class="subtitle">Police, fire or ambulance?</h2><p class="muted">Tap all that apply.</p>'+cards([box("🛡 Police",d().police,"toggle:police"),box("🔥 Fire brigade",d().fire,"toggle:fire"),box("🚑 Ambulance",d().ambulance,"toggle:ambulance"),box("✓ None required",d().none,"toggle:none")])+'<a href="tel:000" class="btn danger block" style="display:block;text-align:center;text-decoration:none">Dial 000</a>';
break;
case 1:
html+='<div class="eyebrow">INCIDENT DETAILS</div><h1 class="title">Who is involved?</h1><p class="muted">What happened?</p>'+opts("type",["Collision","Vehicle damage","Cargo damage","Breakdown","Other"])+f("location","Location","Road, suburb or landmark")+f("when","Date and approximate time","e.g. 6 Nov 10:30am")+'<b>Other people or vehicles involved?</b>'+opts("involved",["Yes","No","Unsure"],3)+f("people","Other drivers or witnesses","Names, regos or contacts if available",true);
break;
case 2:
html+='<div class="eyebrow">IDENTIFICATION</div><h1 class="title">Driver & vehicle details</h1><p class="muted">Enter manually, scan a vehicle QR or request a broker-verified SMS link.</p>'+cards([box("✎ Manual",method==="manual","method:manual"),box("▦ Scan QR",method==="qr","method:qr"),box("✉ SMS BIIMA",method==="sms","method:sms")],3);
if(method==="qr")html+='<div class="panel"><b>Choose a fleet (QR simulation)</b>'+cards(db.fleets.filter(x=>x.active).map(x=>box(x.name,fleetId===x.id,"fleet:"+x.id)))+'<b>Vehicle QR</b><div class="actions">'+db.vehicles.filter(v=>v.active&&v.fleet===fleetId).map(v=>btn("▦ "+v.rego,"qr:"+v.id)).join("")+'</div><p class="hint">Public QR identifies the vehicle/fleet; personal details require verification.</p></div>';
if(method==="sms")html+='<div class="panel"><b>Broker-verified SMS demonstration</b><p class="hint">Enter rego and/or mobile below, then simulate a verified broker reply. No real SMS is sent.</p>'+btn("Simulate broker SMS reply","smsverify","primary block")+'</div>';
html+=(d().fleet?'<div class="panel"><b>✓ '+esc(d().fleet)+'</b></div>':"")+f("name","Driver full name","Name")+f("mobile","Mobile number","04xx xxx xxx")+f("rego","Registration","ABC123")+'<b>Registration state</b>'+opts("state",states,4)+cards([box((d().confirmed?"☑ ":"□ ")+"I confirm these details",d().confirmed,"check:confirmed")]);
break;
case 3:
html+='<div class="eyebrow">EVIDENCE</div><h1 class="title">Photos & notes</h1><p class="muted">Capture real photos and an original voice recording on your device.</p><div class="panel"><b>Photos · '+d().photos.length+'/6</b><div class="field"><label>Add photos</label><input id="photos-input" type="file" multiple accept="image/*"></div><div id="photos-preview" class="photos"></div></div>'+f("notes","Driver notes","Describe what happened",true)+'<b>Voice-note language</b>'+opts("language",["English","Punjabi","Hindi","Other"])+'<div class="panel"><b>Original voice note</b><div class="actions">'+btn(recorder?.state==="recording"?"■ Stop recording":"🎙 Record voice","record","primary")+'</div><div class="field"><label>Or attach existing audio file</label><input type="file" id="audio-input" accept="audio/*"></div><div id="audio-preview"></div></div>';
break;
case 4:
html+='<div class="eyebrow">REVIEW</div><h1 class="title">Review your report</h1><p class="muted">Press Back to edit previous information.</p>'+detail(d());
break;
case 5:
html+='<div class="eyebrow">SUBMIT</div><h1 class="title">Submit to BIIMA</h1>'+row("Driver",d().name)+row("Vehicle",d().rego)+row("Incident",d().type)+'<div class="notice">Demo submission creates a record on this browser only. No BIIMA server acknowledgement or cloud sync.</div>'+cards([box((d().consent?"☑ ":"□ ")+"I confirm the report is accurate",d().consent,"check:consent")])+btn("Complete demo FNOL","submit","primary block");
break;

}
if(message)html+='<div class="notice">'+esc(message)+'</div>';return html+nav()+'</div>';
}
function broker(){if(!auth)return '<div class="card login"><div class="eyebrow">BIIMA · BROKER ADMIN</div><h1 class="title">Broker login</h1><p class="muted">Demo login only—not secure authentication.</p>'+inf("login-email","Work email","demo@biima.example")+'<div class="field"><label>Password</label><input type="password" data-form="login-password"></div>'+btn("Sign in","login","primary block")+'<div class="panel small">demo@biima.example · Demo123!</div>'+(message?'<div class="notice">'+esc(message)+'</div>':"")+'</div>';
let h='<div class="tabs">'+[["dashboard","Dashboard"],["incidents","FNOL reports"],["fleets","Fleets"],["vehicles","Vehicles"],["drivers","Drivers"],["sms","SMS requests"]].map(t=>btn(t[1],"tab:"+t[0],t[0]===tab?"primary":"")).join("")+'</div>';
if(tab==="dashboard")h+='<h1 class="title">BIIMA Admin Dashboard</h1><div class="grid">'+[["incidents","FNOL reports",db.reports.length],["fleets","Fleet clients",db.fleets.length],["vehicles","Vehicles",db.vehicles.length],["drivers","Drivers",db.drivers.length]].map(a=>'<button class="record" data-act="tab:'+a[0]+'"><strong style="font-size:28px">'+a[2]+'</strong><h2 class="subtitle">'+a[1]+'</h2>Open →</button>').join("")+'</div>';
if(tab==="fleets"){const x=db.fleets.find(x=>x.id===editId);h+='<h1 class="title">Fleet registration & maintenance</h1><div class="card">'+inf("fleet-name","Company name",x?.name)+inf("fleet-contact","Primary contact",x?.contact)+'<div class="actions">'+btn("Save fleet","savefleet","primary")+btn("New fleet","newfleet")+(x?btn(x.active?"Deactivate":"Reactivate","togglefleet"):"")+'</div></div><div class="grid">'+db.fleets.map(v=>'<button class="record" data-act="editfleet:'+v.id+'"><b>'+esc(v.name)+'</b><p class="small muted">'+esc(v.contact||"No contact")+'</p><span class="pill">'+(v.active?"Active":"Inactive")+'</span></button>').join("")+'</div>'}
if(tab==="vehicles"){const x=db.vehicles.find(x=>x.id===editId);h+='<h1 class="title">Vehicle registration & maintenance</h1><div class="card">'+inf("vehicle-rego","Registration",x?.rego)+'<b>Vehicle type</b>'+cards(["Truck","Van","Car","Trailer"].map(t=>box(t,type===t,"vtype:"+t)),4)+'<b>Assign to fleet</b>'+cards([box("Independent / unassigned",!assignFleet,"assignfleet:none")].concat(db.fleets.filter(f=>f.active).map(f=>box(f.name,assignFleet===f.id,"assignfleet:"+f.id))))+'<div class="actions">'+btn("Save vehicle","savevehicle","primary")+btn("New vehicle","newvehicle")+(x?btn(x.active?"Deactivate":"Reactivate","togglevehicle"):"")+'</div></div><div class="grid">'+db.vehicles.map(v=>'<button class="record" data-act="editvehicle:'+v.id+'"><b>▦ '+esc(v.rego)+'</b><p class="muted small">'+esc(v.type)+' · '+esc(fleet(v.fleet)?.name||"Independent")+'</p><span class="pill">'+(v.active?"Active":"Inactive")+'</span></button>').join("")+'</div>'}
if(tab==="drivers"){const x=db.drivers.find(x=>x.id===editId);h+='<h1 class="title">Single and fleet drivers</h1><div class="card"><b>Driver type</b>'+cards([box("Single / independent",driverGroup==="single","group:single"),box("Part of a fleet",driverGroup==="fleet","group:fleet")])+inf("driver-name","Full name",x?.name)+inf("driver-mobile","Mobile number",x?.mobile)+(driverGroup==="fleet"?'<b>Fleet</b>'+cards(db.fleets.filter(f=>f.active).map(f=>box(f.name,assignFleet===f.id,"assignfleet:"+f.id))):"")+'<b>Fixed vehicle (optional)</b>'+cards([box("No fixed vehicle",!assignVehicle,"assignvehicle:none")].concat(db.vehicles.filter(v=>v.active&&v.fleet===(driverGroup==="fleet"?assignFleet:"")).map(v=>box(v.rego,assignVehicle===v.id,"assignvehicle:"+v.id))))+'<div class="actions">'+btn("Save driver","savedriver","primary")+btn("New driver","newdriver")+(x?btn(x.active?"Deactivate":"Reactivate","toggledriver"):"")+'</div></div><div class="grid">'+db.drivers.map(v=>'<button class="record" data-act="editdriver:'+v.id+'"><b>'+esc(v.name)+'</b><p class="small muted">'+esc(v.mobile)+'</p><span class="pill">'+esc(fleet(v.fleet)?.name||"Independent")+'</span> · '+esc(veh(v.vehicle)?.rego||"No fixed vehicle")+'</button>').join("")+'</div>'}
if(tab==="sms")h+='<h1 class="title">Broker access requests</h1>'+(db.sms.length?db.sms.map(x=>'<div class="card">'+row("Rego",x.rego)+row("Mobile",x.mobile)+row("Status",x.status)+'</div>').join(""):'<div class="empty">No demo SMS requests received.</div>');
if(tab==="incidents"){const x=db.reports.find(x=>x.id===selected);if(!x)h+='<h1 class="title">Incident queue</h1>'+(db.reports.length?db.reports.map(r=>'<button class="record" style="margin:6px 0" data-act="openincident:'+r.id+'"><b>'+esc(r.id)+'</b><p>'+esc(r.fleet||"Unmatched")+' · '+esc(r.rego)+'</p><span class="pill">'+esc(r.status)+(r.ready?" · Ready for review":"")+'</span><p>View full FNOL details →</p></button>').join(""):'<div class="empty">No submitted demo FNOL records yet.</div>');else h+='<button class="btn" data-act="backqueue">← Incident queue</button><div class="card" style="margin-top:12px"><h1 class="title">'+esc(x.id)+'</h1><span class="pill">'+esc(x.status)+'</span>'+detail(x)+'<div id="broker-evidence" class="panel">Evidence loading…</div><h2 class="subtitle">Broker follow-up</h2>'+(x.question?'<div class="panel"><b>Requested:</b> '+esc(x.question)+'<p><b>Driver response:</b> '+esc(x.response||"Awaiting response")+'</p></div>':"")+(x.status==="New"?btn("Start review","review","primary"):"")+(x.status==="New"||x.status==="In Review"?'<p>'+inf("broker-question","Request more information","")+btn("Save request","ask","")+'</p>':"")+(x.status==="Need More Information"?inf("broker-response","Simulate driver response","")+btn("Receive response","respond"):"")+(x.ready?btn("Acknowledge new information","ack"):"")+(x.status==="In Review"&&!x.ready?btn("Approve FNOL intake","approve","primary"):"")+'<p class="hint">Approval refers to FNOL intake, not insurance coverage.</p></div>'}
if(message)h+='<div class="notice">'+esc(message)+'</div>';h+='<p>'+btn("Sign out","logout")+'</p>';return h;
}
function render(){document.getElementById("root").innerHTML=section==="broker"?broker():driver();$("driver-nav").classList.toggle("active",section!=="broker");$("broker-nav").classList.toggle("active",section==="broker");if(section==="driver"&&d().step===3)loadEvidence();if(section==="broker"&&tab==="incidents"&&selected)loadBrokerEvidence()}
function note(x){message=x;render()}
function put(k,v){d()[k]=v;if(["name","mobile","rego","state"].includes(k))d().confirmed=false;save();render()}
function toggle(k){if(k==="none"){d().none=!d().none;d().police=d().fire=d().ambulance=false}else{d()[k]=!d()[k];d().none=false}save();render()}
function next(){
if(d().step===0&&(!d().safe||!d().injuries||(!d().none&&!d().police&&!d().fire&&!d().ambulance)))return note("Complete the safety, injuries and emergency services questions.");
if(d().step===1&&!d().type)return note("Select incident type.");
if(d().step===2&&(!d().name.trim()||!d().mobile.trim()||!d().rego.trim()||!d().confirmed))return note("Enter and confirm the driver's name, mobile and registration.");
d().step=Math.min(5,d().step+1);save();message="";render();window.scrollTo(0,0)
}
function prefill(v,source){const x=veh(v),f=fleet(x?.fleet);if(!x)return;d().rego=x.rego;d().fleet=f?.name||"";d().source=source;d().confirmed=false;const person=db.drivers.find(p=>p.vehicle===v&&p.active);if(person){d().name=person.name;d().mobile=person.mobile}method="manual";save();note(source==="Vehicle QR"?"Demo QR lookup populated the associated driver details. In production, personal information requires verification. Please confirm.":"Demo broker verification completed. Driver details have been populated; check and confirm them.")}
function smsVerify(){const reg=d().rego.trim().toUpperCase(),mob=d().mobile.replace(/\D/g,"");if(!reg&&!mob)return note("Enter rego or mobile to request a demo code.");const person=db.drivers.find(x=>x.active&&((mob&&x.mobile.replace(/\D/g,"")===mob)||(reg&&veh(x.vehicle)?.rego===reg)));db.sms.unshift({rego:reg,mobile:d().mobile,status:person?"Demo verified":"Pending"});save();if(person){prefill(person.vehicle,"Broker-verified SMS");d().name=person.name;d().mobile=person.mobile;save()}else note("Request added to broker inbox. No verified match. No real SMS was sent.")}
function form(name){return document.querySelector('[data-form="'+name+'"]')?.value.trim()||""}
function id(prefix){return prefix+Date.now().toString(36)}
function saveFleet(){const name=form("fleet-name");if(!name)return note("Fleet name required");const f={name,contact:form("fleet-contact")};if(editId)db.fleets=db.fleets.map(x=>x.id===editId?{...x,...f}:x);else{editId=id("f");db.fleets.push({id:editId,...f,active:true})}save();note("Fleet saved.")}
function saveVehicle(){const rego=form("vehicle-rego").toUpperCase();if(!rego)return note("Registration required.");if(db.vehicles.some(x=>x.rego===rego&&x.id!==editId))return note("Rego already registered.");const v={rego,type,fleet:assignFleet};if(editId)db.vehicles=db.vehicles.map(x=>x.id===editId?{...x,...v}:x);else{editId=id("v");db.vehicles.push({id:editId,...v,active:true})}save();note("Vehicle saved.")}
function saveDriver(){const name=form("driver-name"),mobile=form("driver-mobile");if(!name||!mobile)return note("Name and mobile required.");if(driverGroup==="fleet"&&!assignFleet)return note("Select a fleet.");const person={name,mobile,fleet:driverGroup==="fleet"?assignFleet:"",vehicle:assignVehicle};if(editId)db.drivers=db.drivers.map(x=>x.id===editId?{...x,...person}:x);else{editId=id("d");db.drivers.push({id:editId,...person,active:true})}save();note("Driver saved.")}
function edit(which,record){editId=record;const x=which==="fleets"?fleet(record):which==="vehicles"?veh(record):drv(record);if(which==="vehicles"){type=x.type;assignFleet=x.fleet}else if(which==="drivers"){driverGroup=x.fleet?"fleet":"single";assignFleet=x.fleet;assignVehicle=x.vehicle}render()}
function changeReport(props){db.reports=db.reports.map(x=>x.id===selected?{...x,...props}:x);save();message="";render()}
function acceptSubmit(){if(!d().consent)return note("Confirm the report first.");db.reports.unshift({...JSON.parse(JSON.stringify(d())),id:"FNOL-DEMO-"+Date.now().toString(36),status:"New",ready:false,question:"",response:""});selected=db.reports[0].id;db.draft=blank();save();section="done";message="";render()}
const evidenceStore="biima-preview-media-v1";function mediaDb(){return new Promise((ok,no)=>{const q=indexedDB.open(evidenceStore,1);q.onupgradeneeded=()=>q.result.createObjectStore("blobs");q.onerror=()=>no(q.error);q.onsuccess=()=>ok(q.result)})}
async function storeBlob(file){const dbx=await mediaDb(),key="m"+Date.now()+Math.random().toString(36).slice(2);return new Promise((ok,no)=>{const tx=dbx.transaction("blobs","readwrite");tx.objectStore("blobs").put(file,key);tx.oncomplete=()=>{dbx.close();ok(key)};tx.onerror=()=>{dbx.close();no(tx.error)}})}
async function readBlob(key){const dbx=await mediaDb();return new Promise((ok,no)=>{const q=dbx.transaction("blobs","readonly").objectStore("blobs").get(key);q.onsuccess=()=>{dbx.close();ok(q.result)};q.onerror=()=>{dbx.close();no(q.error)}})}
const urls=new Map();async function mediaUrl(key){if(urls.has(key))return urls.get(key);const blob=await readBlob(key);if(!blob)return"";const url=URL.createObjectURL(blob);urls.set(key,url);return url}
async function addPhotos(files){for(const item of [...files].slice(0,Math.max(0,6-d().photos.length))){if(!item.type.startsWith("image/")||item.size>10e6)continue;try{d().photos.push(await storeBlob(item))}catch{message="Could not save an image locally."}}save();render()}
async function addAudio(file){if(!file)return;try{d().audio=await storeBlob(file);save();render()}catch{note("Could not save recording to browser storage.")}}
async function record(){if(recorder&&recorder.state==="recording"){recorder.stop();return}if(!navigator.mediaDevices?.getUserMedia||!window.MediaRecorder)return note("Recording not supported in this browser. Attach audio instead.");try{stream=await navigator.mediaDevices.getUserMedia({audio:true});let chunks=[];recorder=new MediaRecorder(stream);recorder.ondataavailable=e=>{if(e.data.size)chunks.push(e.data)};recorder.onstop=async()=>{stream.getTracks().forEach(x=>x.stop());await addAudio(new Blob(chunks,{type:recorder.mimeType||"audio/webm"}))};recorder.start();render()}catch{note("Microphone access unavailable. You can attach an existing voice note.")}}
async function loadEvidence(){let photos=[];for(const id of d().photos){try{photos.push('<img alt="Accident photo" src="'+await mediaUrl(id)+'">')}catch{}}if($("photos-preview"))$("photos-preview").innerHTML=photos.join("");let url="";try{url=d().audio?await mediaUrl(d().audio):""}catch{}if($("audio-preview"))$("audio-preview").innerHTML=url?'<audio controls src="'+url+'"></audio>':""}
async function loadBrokerEvidence(){const target=$("broker-evidence"),report=db.reports.find(x=>x.id===selected);if(!target||!report)return;let photos=[];for(const id of report.photos||[]){try{photos.push('<img alt="Evidence photo" src="'+await mediaUrl(id)+'">')}catch{}}let audio="";try{audio=report.audio?await mediaUrl(report.audio):""}catch{}if($("broker-evidence"))$("broker-evidence").innerHTML='<b>Original evidence</b><div class="photos">'+photos.join("")+'</div>'+(audio?'<audio controls src="'+audio+'"></audio>':"")+'<p class="hint">Demo files stored in this browser only.</p>'}
document.addEventListener("click",e=>{const b=e.target.closest("[data-act]");if(!b)return;const action=b.dataset.act,parts=action.split(":"),a=parts[0],v=parts.slice(1).join(":");
if(a==="driver"){section=d().step>0?"resume":"driver"}
else if(a==="broker"){section="broker"}
else if(a==="theme"){dark=!dark;document.documentElement.classList.toggle("dark",dark);localStorage.setItem(KEY+"-dark",dark?"yes":"no")}
else if(a==="set"){put(parts[1],parts.slice(2).join(":"));return}
else if(a==="toggle"){toggle(v);return}
else if(a==="check"){put(v,!d()[v]);return}
else if(a==="resume"){section="driver"}
else if(a==="saved"){section="saved"}
else if(a==="new"){if(d().step>0&&!confirm("Replace the current unfinished demo report?"))return;db.draft=blank();save();section="driver"}
else if(a==="next"){next();return}
else if(a==="back"){d().step=Math.max(0,d().step-1);save()}
else if(a==="method"){method=v}
else if(a==="fleet"){fleetId=v}
else if(a==="qr"){prefill(v,"Vehicle QR");return}
else if(a==="smsverify"){smsVerify();return}
else if(a==="record"){record();return}
else if(a==="submit"){acceptSubmit();return}
else if(a==="login"){if(form("login-email")==="demo@biima.example"&&form("login-password")==="Demo123!"){auth=true;tab="dashboard";message=""}else return note("Use the displayed demo login credentials.")}
else if(a==="logout"){auth=false}
else if(a==="tab"){tab=v;editId="";selected="";message="";assignFleet="";assignVehicle="";driverGroup="single"}
else if(a==="editfleet"||a==="editvehicle"||a==="editdriver"){edit(a.slice(4)+"s",v);return}
else if(a==="newfleet"||a==="newvehicle"||a==="newdriver"){editId="";assignFleet="";assignVehicle="";type="Truck";driverGroup="single"}
else if(a==="savefleet"){saveFleet();return}
else if(a==="savevehicle"){saveVehicle();return}
else if(a==="savedriver"){saveDriver();return}
else if(a==="vtype"){type=v}
else if(a==="assignfleet"){assignFleet=v==="none"?"":v}
else if(a==="assignvehicle"){assignVehicle=v==="none"?"":v}
else if(a==="group"){driverGroup=v;assignFleet="";assignVehicle=""}
else if(["togglefleet","togglevehicle","toggledriver"].includes(a)){const k=a.slice(6)+"s";db[k]=db[k].map(x=>x.id===editId?{...x,active:!x.active}:x);save()}
else if(a==="openincident"){selected=v}
else if(a==="backqueue"){selected=""}
else if(a==="review"){changeReport({status:"In Review"});return}
else if(a==="ask"){const q=form("broker-question");if(!q)return note("Enter a question.");changeReport({status:"Need More Information",question:q,response:"",ready:false});return}
else if(a==="respond"){const q=form("broker-response");if(!q)return note("Enter the driver's response.");changeReport({status:"In Review",response:q,ready:true});return}
else if(a==="ack"){changeReport({ready:false});return}
else if(a==="approve"){changeReport({status:"Approved"});return}
message="";render()});
document.addEventListener("input",e=>{const k=e.target.dataset.input;if(k){d()[k]=e.target.value;if(["name","mobile","rego"].includes(k))d().confirmed=false;save()}});
document.addEventListener("change",e=>{if(e.target.id==="photos-input")addPhotos(e.target.files);if(e.target.id==="audio-input")addAudio(e.target.files[0])});
render();
})();