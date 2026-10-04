const $=id=>document.getElementById(id);
const f=(s,...v)=>s.replace(/\{(\d)\}/g,(_,i)=>v[i]);
const confirmbox=$('confirm');
function ask(text){$('confirmtext').textContent=text;confirmbox.returnValue='';
confirmbox.showModal();$('confirmno').focus();
return new Promise(done=>confirmbox.addEventListener('close',()=>done(confirmbox.returnValue==='yes'),{once:true}));}

const pin=$('pin'),devname=$('devname'),said=$('said'),card=$('pair'),who=$('who'),
clients=$('clients');
async function reloadClients(){clients.innerHTML=await (await fetch('/?clients=1')).text();}
async function sendPin(){
if(!/^\d{4}$/.test(pin.value)){said.textContent='[[A code is four digits.]]';pin.focus();return;}
said.textContent='[[sending…]]';
const r=await fetch('/?pin='+encodeURIComponent(pin.value)+'&name='+encodeURIComponent(devname.value));
said.textContent=await r.text();pin.value='';
setTimeout(reloadClients,1500);setTimeout(reloadClients,5000);}
$('pairsave').addEventListener('click',sendPin);
$('paircancel').addEventListener('click',async()=>{
said.textContent='[[cancelling…]]';pin.value='';
said.textContent=await (await fetch('/?cancelpair=1')).text();});
pin.addEventListener('keydown',e=>{if(e.key==='Enter')sendPin();});
devname.addEventListener('keydown',e=>{if(e.key==='Enter')pin.focus();});
let wasWaiting=!card.classList.contains('off');
setInterval(async()=>{
const name=await (await fetch('/?waiting=1')).text();
const wanted=name.length>0;
if(wanted!==wasWaiting){
card.classList.toggle('off',!wanted);
if(wanted){said.textContent='';devname.focus();}
else reloadClients();
wasWaiting=wanted;}
if(wanted)who.innerHTML=f('[[A device calling itself <b>{0}</b> wants to pair with this machine.]]',name.replace(/&/g,'&amp;').replace(/</g,'&lt;'));
const both=(await (await fetch('/?status=1')).text()).split('\n');
$('host').innerHTML=both[0];$('status').innerHTML=both[1]||'';
document.querySelectorAll('#games .game').forEach(el=>el.classList.toggle('running',el.dataset.id===(both[2]||'')));
available(both[3]||'');
if(!update.dataset.busy&&both[4]&&both[4]!==update.dataset.last){update.innerHTML=both[4];update.dataset.last=both[4];}
const theme=(await (await fetch('/?theme=1')).text()).trim();
if(theme&&document.documentElement.dataset.theme!==theme)document.documentElement.dataset.theme=theme;
},1000);
if(!card.classList.contains('off'))devname.focus();

const al=$('autologon');if(al){$('alopen').addEventListener('click',async e=>{
e.target.disabled=true;
$('alsaid').textContent=await (await fetch('/?autologon=setup')).text();
const until=Date.now()+600000;const poll=setInterval(async()=>{
if(Date.now()>until){clearInterval(poll);e.target.disabled=false;return;}
if((await (await fetch('/?autologon=state')).text()).trim()==='on'){clearInterval(poll);
al.innerHTML='<p>[[Automatic sign-in is on now. This host comes back on its own after a restart.]]</p>';}
},5000);});}

const diag=$('diag'),log=$('log'),connlog=$('connlog'),output=$('output'),screenpane=$('pane-screen'),
shot=$('shot'),shotimg=$('shotimg'),shotsaid=$('shotsaid');
let pane='',shotUrl='';
function available(list){const have=new Set(('log,connections,'+list).split(','));
document.querySelectorAll('#diagbar button[data-pane]').forEach(b=>b.hidden=!have.has(b.dataset.pane));
screenpane.classList.toggle('with-output',have.has('output'));
if(pane&&!have.has(pane))openPane('');}
function openPane(name){pane=name;
document.querySelectorAll('#diagbar button[data-pane]').forEach(b=>b.classList.toggle('on',b.dataset.pane===name));
document.querySelectorAll('#diag .pane').forEach(p=>p.hidden=p.id!=='pane-'+name);
diag.classList.toggle('open',!!name);
if(!name)shot.classList.remove('full');
if(name==='screen')refreshShot();
refreshPane(true);}
$('diagbar').addEventListener('click',e=>{const b=e.target.closest('button[data-pane]');if(!b)return;
openPane(pane===b.dataset.pane?'':b.dataset.pane);});
async function fill(pre,url,toEnd){const atEnd=toEnd||pre.scrollTop+pre.clientHeight>=pre.scrollHeight-8;
pre.textContent=await (await fetch(url)).text();if(atEnd)pre.scrollTop=pre.scrollHeight;}
async function refreshPane(toEnd){try{
if(pane==='log')await fill(log,'/?log=1',toEnd);
if(pane==='connections'){await fill(connlog,'/?connections=1',toEnd);await reloadBlocked();await reloadClients();}
if(pane==='screen'&&screenpane.classList.contains('with-output'))await fill(output,'/?output=1',toEnd);
}catch{}}
setInterval(()=>refreshPane(false),3000);
setInterval(()=>{if(pane==='screen')refreshShot();},5000);
available(diag.dataset.available);
async function refreshShot(){
try{const r=await fetch('/?screen=1&v='+Date.now());
if((r.headers.get('Content-Type')||'').startsWith('image/')){const url=URL.createObjectURL(await r.blob());
shotimg.onload=()=>{if(shotUrl)URL.revokeObjectURL(shotUrl);shotUrl=url;};shotimg.src=url;shot.classList.add('has');}
else{shot.classList.remove('has','full');shotsaid.textContent=await r.text();}}catch{}}
shot.addEventListener('click',e=>{
if(e.target.id==='shotclose'){shot.classList.remove('full');return;}
if(shot.classList.contains('has'))shot.classList.add('full');});
document.addEventListener('keydown',e=>{if(e.key==='Escape')shot.classList.remove('full');});

const games=$('games'),editor=$('editor'),title=$('title'),command=$('command'),folder=$('folder'),
editorsaid=$('editorsaid'),arturl=$('arturl'),args=$('args'),nostream=$('nostream'),
preview=$('preview'),streamtab=$('streamtab');
let editing=0,pending=null,previewUrl='';
function tab(name){document.querySelectorAll('#tabs button').forEach(b=>b.classList.toggle('on',b.dataset.tab===name));
document.querySelectorAll('#editor .tab').forEach(t=>t.hidden=t.id!=='tab-'+name);}
$('tabs').addEventListener('click',e=>{const b=e.target.closest('button[data-tab]');if(b)tab(b.dataset.tab);});
function showKind(){const off=nostream.checked;streamtab.hidden=off;$('find').hidden=off;
if(off&&streamtab.classList.contains('on'))tab('main');}
nostream.addEventListener('change',showKind);
function show(src){if(previewUrl&&src!==previewUrl){URL.revokeObjectURL(previewUrl);previewUrl='';}
preview.style.backgroundImage=src?'url("'+src+'")':'';preview.classList.toggle('has',!!src);}
function showStored(){if(!editing){show('');return;}
const probe=new Image();probe.onload=()=>show(probe.src);probe.onerror=()=>show('');
probe.src='/?cover='+editing+'&v='+Date.now();}
async function sendCover(id,cover){
const r=cover.blob?await fetch('/?upload='+id,{method:'POST',body:cover.blob})
:await fetch('/?arturl='+id+'&url='+encodeURIComponent(cover.url));
return r.text();}
async function setCover(cover){
if(!editing){pending=cover;
if(cover.blob){show('');previewUrl=URL.createObjectURL(cover.blob);show(previewUrl);}else show(cover.url);
editorsaid.textContent='[[The cover is added when the game is saved.]]';return;}
editorsaid.textContent=cover.blob?'[[uploading…]]':'[[fetching…]]';
editorsaid.textContent=await sendCover(editing,cover);showStored();await reload();}
async function reload(){games.innerHTML=await (await fetch('/?games=1')).text();}
const scansaid=$('scansaid');
$('rescan').addEventListener('click',async e=>{const b=e.target;b.disabled=true;
scansaid.textContent=await (await fetch('/?rescan=1')).text();
setTimeout(reload,2000);
setTimeout(()=>{reload();scansaid.textContent='';b.disabled=false;},6000);});
$('hosttools').addEventListener('click',async e=>{const b=e.target.closest('button[data-power]');if(!b)return;
const power=b.dataset.power;
if(!await ask(power==='restart'?'[[Are you sure you want to restart the host? Running games and streams will be closed.]]':'[[Are you sure you want to shut down the host? It cannot be switched on again from here.]]'))return;
const tools=document.querySelectorAll('#hosttools button');tools.forEach(b=>b.disabled=true);
setTimeout(()=>tools.forEach(b=>b.disabled=false),15000);
try{scansaid.textContent=await (await fetch('/?power='+power,{method:'POST'})).text();}
catch{scansaid.textContent='[[The host did not answer.]]';}});
const update=$('update');
update.addEventListener('click',async e=>{const link=e.target.closest('#checkversion');if(!link)return;e.preventDefault();
if(link.dataset.busy)return;link.dataset.busy=update.dataset.busy='1';
link.textContent='[[Checking…]]';
try{link.textContent=await (await fetch('/?checkupdate=1')).text();}
catch{link.textContent='[[Check failed]]';}
delete update.dataset.busy;
setTimeout(()=>{link.textContent='[[Has new version?]]';delete link.dataset.busy;},5000);});
function open(id,name,starts,from,level,cardOn,extra,noStream){editing=id;
$('editortitle').textContent=id?'[[Edit game]]':'[[Add a game]]';
title.value=name||'';command.value=starts||'';folder.value=from||'';args.value=extra||'';
nostream.checked=noStream==='1';showKind();
quality.value=level===undefined?2:level;
(document.querySelector('#splash input[value="'+cardOn+'"]')||document.querySelector('#splash input[value="1"]')).checked=true;
editorsaid.textContent='';
arturl.value='';pending=null;tab('main');showStored();
editor.showModal();title.focus();}
$('cancel').addEventListener('click',()=>editor.close());
$('save').addEventListener('click',async()=>{
if(!title.value.trim()||!command.value.trim()){
editorsaid.textContent='[[A game needs a name and something to start.]]';return;}
const r=await fetch('/?save='+editing+'&title='+encodeURIComponent(title.value)+'&command='+encodeURIComponent(command.value)+'&folder='+encodeURIComponent(folder.value)+'&args='+encodeURIComponent(args.value)+'&nostream='+(nostream.checked?1:0)+'&quality='+quality.value+'&card='+document.querySelector('#splash input:checked').value);
const [said,id]=(await r.text()).split('\n');editorsaid.textContent=said;
if(!id)return;
if(pending){editorsaid.textContent=await sendCover(id,pending);pending=null;}
await reload();editor.close();});
const picker=$('picker'),pickname=$('pickname'),pickgrid=$('pickgrid'),
picksaid=$('picksaid');
async function search(){const name=pickname.value.trim();
if(!name){picksaid.textContent='[[Type a name to look for.]]';pickname.focus();return;}
picksaid.textContent='[[looking…]]';pickgrid.innerHTML='';
const found=await (await fetch('/?artlist=1&title='+encodeURIComponent(name))).json();
const none=f('[[Nothing with a picture was found under "{0}". Try the name a store would use.]]',name);
if(!found.length){picksaid.textContent=none;return;}
const count=()=>{const n=pickgrid.children.length;
picksaid.textContent=n?f('[[{0} found — click the right one]]',n):none;};
count();
for(const c of found){const b=document.createElement('button');b.type='button';b.className='pick';
b.title=c.name;
const img=document.createElement('img');img.loading='lazy';img.alt='';img.src=c.src;
img.onerror=()=>{b.remove();count();};
const cap=document.createElement('span');cap.textContent=c.name;
b.append(img,cap);pickgrid.append(b);}
count();}
$('find').addEventListener('click',()=>{pickname.value=title.value.trim();
picksaid.textContent='';pickgrid.innerHTML='';picker.showModal();search();});
$('picksearch').addEventListener('click',search);
pickname.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();search();}});
pickgrid.addEventListener('click',async e=>{const b=e.target.closest('.pick');if(!b)return;
const img=b.querySelector('img');if(!img)return;
picker.close();await setCover({url:img.currentSrc||img.src});});
picker.addEventListener('click',e=>{if(e.target===picker)picker.close();});
$('fetch').addEventListener('click',()=>{const url=arturl.value.trim();
if(!url){editorsaid.textContent='[[Paste the address of a picture.]]';return;}
setCover({url});});
$('file').addEventListener('change',async e=>{
if(!e.target.files.length)return;const blob=e.target.files[0];e.target.value='';
await setCover({blob});});
editor.addEventListener('paste',e=>{
const item=[...(e.clipboardData?e.clipboardData.items:[])].find(i=>i.type.startsWith('image/'));
if(!item)return;e.preventDefault();tab('art');setCover({blob:item.getAsFile()});});

games.addEventListener('click',async e=>{
if(e.target.closest('#addtile')){open(0);return;}
const button=e.target.closest('button[data-do]');if(!button)return;
const tile=button.closest('.game');
if(button.dataset.do==='edit'){open(tile.dataset.id,tile.dataset.title,tile.dataset.command,
tile.dataset.folder,tile.dataset.quality,tile.dataset.card,tile.dataset.args,
tile.dataset.nostream);return;}
if(button.dataset.do==='start'){button.disabled=true;
scansaid.textContent=await (await fetch('/?start='+tile.dataset.id)).text();
await reload();setTimeout(()=>{scansaid.textContent='';},5000);return;}
if(button.dataset.do==='remove'){
if(!await ask(f('[[Remove {0} from the list?]]',tile.dataset.title)))return;
await fetch('/?remove='+tile.dataset.id);await reload();return;}
if(button.dataset.do==='reset'){
if(!await ask(f('[[Reset {0} to what was found? Its name, command, folder, cover and settings go back to the defaults.]]',tile.dataset.title)))return;
button.disabled=true;scansaid.textContent=await (await fetch('/?reset='+tile.dataset.id)).text();
setTimeout(reload,2000);setTimeout(()=>{reload();scansaid.textContent='';},6000);return;}
if(button.dataset.do==='stop'){
if(!await ask(f('[[Stop {0}?]]',tile.dataset.title)))return;
await fetch('/?stop=1');await reload();}});

clients.addEventListener('click',async e=>{
const button=e.target.closest('button[data-do=forget]');if(!button)return;
const row=button.closest('.client');
if(!await ask(f('[[Forget {0}? It will have to pair again.]]',row.dataset.name)))return;
await fetch('/?forget='+row.dataset.id);
await reloadClients();});

const blocked=$('blocked');
async function reloadBlocked(){blocked.innerHTML=await (await fetch('/?blocked=1')).text();}
blocked.addEventListener('click',async e=>{
const button=e.target.closest('button[data-do=unblock]');if(!button)return;
const row=button.closest('[data-ip]');
if(!await ask(f('[[Let {0} back in? It is counted from nothing again.]]',row.dataset.ip)))return;
await fetch('/?unblock='+encodeURIComponent(row.dataset.ip));
await reloadBlocked();});
