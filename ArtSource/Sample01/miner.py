"""Neckless green bean miner. Rigid articulated parts reuse the game's existing IK.
The original turnaround is the silhouette reference; no humanoid head/neck rig.
"""
import math
from props import _tube

def build_miner(u):
    root=u.group('BeanMiner');body=u.group('Body',root)
    u.lathe('Continuous green bean',[.54,.60,.76,.98,1.17,1.34,1.48,1.58,1.625],
        [.12,.30,.365,.386,.349,.294,.262,.175,0], [.14,.225,.269,.286,.27,.244,.218,.158,0],
        'skin',body,segments=40,centers=[0,0,0,0,.012,.015,.006,0,0])
    u.ellipsoid('Pale bean belly',(0,.986,.270),(.356,.406,.053),'belly',body,32,20)
    u.lathe('Tailored short jacket',[.78,.805,.94,1.115,1.205], [.378,.400,.416,.384,.32],
        [.283,.300,.303,.288,.265],'cloth',body,40,27,333)
    u.lathe('Folded jacket hem',[.783,.813,.832],[.385,.405,.406],[.293,.312,.31],
        'cloth_light',body,40,27,333)
    for side in [-1,1]:
        # Broad angular collar with a narrow turned edge, instead of floating spheres.
        front=[(side*.135,1.17,.29),(side*.245,1.235,.225),(side*.348,1.18,.196),(side*.286,1.054,.258)]
        verts=front+[(x,y,z-.027) for x,y,z in front]
        u.mesh('Jacket lapel',verts,[(0,1,2,3),(7,6,5,4),(0,4,5,1),(1,5,6,2),(2,6,7,3),(3,7,4,0)],'cloth_light',body)
        u.beam('Lapel sewn edge',front[0],front[3],.012,.012,'seam',body,.003)
        u.box('Chest patch pocket',(side*.276,.982,.272),(.133,.154,.035),'cloth_light',body,.016)
        u.box('Chest pocket flap',(side*.276,1.044,.296),(.145,.045,.025),'cloth',body,.01)
        u.ellipsoid('Pocket brass snap',(side*.276,1.039,.313),(.027,.027,.013),'iron_edge',body,12,8)
        for n in range(3):u.ellipsoid('Jacket brass button',(side*.162,.86+n*.115,.291),(.026,.028,.012),'iron_edge',body,12,8)
        for n in range(5):
            u.box('Pocket stitch',(side*.276-.047+n*.023,.92,.293),(.01,.004,.006),'seam',body,.001)
    # Back yoke and readable horizontal seam from the main back view.
    u.lathe('Back shoulder yoke',[1.12,1.156],[.375,.362],[.292,.28],'cloth_light',body,20,105,255)
    for n in range(13):
        a=math.radians(125+n*9)
        u.ellipsoid('Back hem stitch',(math.sin(a)*.403,.81,math.cos(a)*.314),(.013,.006,.009),'seam',body,8,6)
    u.ellipsoid('Trouser seat',(0,.59,-.016),(.66,.247,.47),'leather',body,24,14)
    u.lathe('Leather waist belt',[.661,.676,.724,.735],[.344,.354,.354,.34],[.258,.268,.268,.254],'leather_dark',body,36)
    u.box('Brass buckle',(0,.698,.278),(.14,.08,.027),'iron_edge',body,.013)
    u.box('Buckle hollow',(0,.698,.297),(.082,.043,.009),'leather_dark',body,.005)
    u.box('Buckle pin',(.013,.7,.304),(.016,.047,.008),'iron_edge',body,.003)
    for x in [-.26,-.15,.15,.26]:u.box('Belt loop',(x,.70,.23),(.034,.083,.032),'leather_light',body,.008)
    # The face is broad and low; pupils are large, with one small catch-light.
    for side,label in [(-1,'Left'),(1,'Right')]:
        u.ellipsoid(label+' eye socket',(side*.109,1.43,.219),(.143,.156,.062),'skin_light',body,28,16)
        u.ellipsoid(label+' eye white',(side*.109,1.433,.238),(.116,.126,.038),'eye',body,28,16)
        u.ellipsoid(label+' iris',(side*.109,1.435,.257),(.091,.100,.023),'iris',body,24,16)
        u.ellipsoid(label+' pupil',(side*.109,1.441,.270),(.074,.082,.018),'pupil',body,24,16)
        u.ellipsoid(label+' catchlight',(side*.109-.019,1.465,.281),(.025,.027,.008),'white',body,12,8)
        lid=u.group(label+'Lid',body,(side*.109,1.478,.274))
        u.ellipsoid(label+' soft eyelid',(0,0,0),(.145,.128,.023),'skin',lid,24,14)
        lid.scale=(1,1,.02)  # Blender Z = Unity Y; runtime controls blink scale.
        cheek=u.group(label+'Cheek',body,(side*.145,1.306,.211))
        u.ellipsoid(label+' cheek flesh',(0,0,0),(.12,.12,.055),'skin',cheek,24,16)
    mouth=u.group('Mouth',body,(0,1.318,.287))
    u.ellipsoid('Small neutral mouth',(0,0,0),(.10,.012,.013),'pupil',mouth,20,10)
    # Helmet shell, rolled brim, wrap strap and a separate lamp ring silhouette.
    u.lathe('Domed hardhat',[1.551,1.61,1.69,1.76,1.802,1.813],
        [.318,.317,.272,.207,.113,0],[.273,.274,.243,.195,.115,0], 'hat',body,40)
    u.lathe('Rolled hardhat brim',[1.535,1.543,1.558,1.574],
        [.358,.385,.380,.325],[.315,.346,.344,.283], 'hat',body,40)
    u.lathe('Helmet brim edge',[1.540,1.550],[.386,.386],[.347,.347],'hat_light',body,40)
    u.lathe('Dark lamp strap',[1.578,1.625],[.324,.317],[.283,.281],'leather_dark',body,40)
    u.ellipsoid('Helmet reinforcing ridge',(0,1.747,-.014),(.073,.148,.417),'hat_light',body,24,16)
    for side in [-1,1]:
        u.box('Lamp strap buckle',(side*.172,1.601,.234),(.05,.072,.038),'hat_light',body,.009)
    for depth,radius,color in [(.292,.115,'leather_dark'),(.34,.100,'iron_edge'),(.353,.085,'leather_dark'),(.369,.073,'lamp')]:
        u.cylinder('Miner lamp ring',(0,1.683,depth),radius,.024,color,body,axis=(0,0,1),vertices=32)
    u.ellipsoid('Lens reflection',(-.018,1.710,.383),(.065,.067,.015),'white',body,20,12)
    # Small scuffs are intentionally sparse; they describe hardhat wear, not noise.
    for x,y,z in [(-.19,1.686,.206),(-.22,1.665,.187),(.225,1.663,.18)]:
        u.mesh('Hardhat wear',[(x-.008,y,z),(x+.017,y+.013,z-.008),(x+.01,y+.033,z-.02),(x-.012,y+.023,z-.01)],[(0,1,2,3)],'rust',body)
    for side,label in [(-1,'Left'),(1,'Right')]:
        shoulder=u.group(label+'Shoulder',body,(side*.355,1.185,-.006))
        u.ellipsoid('Rounded jacket upper sleeve',(0,-.092,0),(.266,.295,.272),'cloth',shoulder,24,16)
        u.cylinder('Rolled fabric cuff',(0,-.206,0),.131,.079,'cloth_light',shoulder,vertices=24)
        u.cylinder('Cuff pale turned edge',(0,-.242,0),.128,.018,'seam',shoulder,vertices=24)
        elbow=u.group(label+'Elbow',shoulder,(0,-.25,0))
        u.ellipsoid('Green forearm',(0,-.073,0),(.158,.20,.169),'skin',elbow,24,14)
        hand=u.group(label+'Hand',elbow,(0,-.25,0))
        u.cylinder('Glove rolled cuff',(0,.07,0),.105,.075,'leather_dark',hand,vertices=20)
        u.ellipsoid('Soft glove palm',(0,-.016,-.022),(.184,.198,.080),'leather',hand,20,12)
        for f in range(4):
            yy=-.077+f*.041
            _tube(u,'Curled work glove finger',[(side*.06,yy,0),(side*.08,yy,.030),(side*.061,yy,.075),(side*.02,yy,.084),(0,yy,.071)],(.021,.022,.021,.020,.017),'leather',hand)
        _tube(u,'Opposing work glove thumb',[(-side*.068,.008,-.009),(-side*.083,.03,.024),(-side*.051,.055,.072),(-side*.017,.05,.070)],(.026,.026,.024,.02),'leather',hand)
        u.box('Stitched glove back',(0,.005,-.077),(.116,.10,.019),'leather_light',hand,.018)
        for n in range(4):u.box('Glove stitches',(-.038+n*.025,.047,-.09),(.01,.004,.005),'rope',hand,.001)
        hip=u.group(label+'Hip',body,(side*.171,.52,0))
        u.ellipsoid('Rounded work trousers',(0,-.088,-.005),(.283,.302,.303),'leather',hip,24,16)
        u.box('Cargo pocket',(side*.128,-.072,.03),(.07,.167,.167),'leather_light',hip,.025)
        u.box('Cargo pocket flap',(side*.144,-.011,.039),(.052,.05,.179),'leather',hip,.016)
        knee=u.group(label+'Knee',hip,(0,-.21,0))
        u.cylinder('Trouser cuff',(0,-.028,0),.118,.12,'leather_dark',knee,vertices=24)
        u.ellipsoid('Boot upper',(0,-.155,.042),(.257,.233,.336),'boot',knee,28,16)
        u.ellipsoid('Broad boot toe',(0,-.192,.10),(.292,.167,.292),'boot',knee,28,16)
        u.box('Boot outsole',(0,-.293,.060),(.299,.067,.376),'sole',knee,.026)
        u.box('Boot welt',(0,-.251,.061),(.307,.032,.381),'leather_dark',knee,.016)
        for xx in [-.122,0,.122]:
            for zz in [-.074,.07,.20]:u.box('Sole tread',(xx,-.317,zz),(.053,.032,.053),'sole',knee,.007)
        for n in range(3):
            z=.08+n*.027;y=-.107-n*.026
            u.beam('Boot lace',(-.07,y,z),(.07,y-.007,z+.007),.012,.012,'hat',knee,.004)
            for side2 in [-1,1]:u.ellipsoid('Boot eyelet',(side2*.074,y,z),(.021,.018,.017),'iron_edge',knee,12,8)
        for obj in knee.children:
            if not obj.name.startswith('Trouser cuff'):obj.location.z+=.024
    # Cosmetics share the base body and never change rules or skeleton.
    scarf=u.group('VariantScarf',body)
    u.mesh('Small neckerchief',[(-.22,1.19,.29),(.22,1.19,.29),(0,1.02,.332),(0,1.25,.24)],[(0,1,2),(0,3,1)],'hat',scarf)
    badge=u.group('VariantGoggles',body)
    for x in [-.12,.12]:
        u.cylinder('Helmet spare goggle rim',(x,1.704,.27),.069,.035,'leather_dark',badge,axis=(0,0,1),vertices=20)
        u.cylinder('Helmet spare goggle glass',(x,1.704,.29),.052,.012,'blue',badge,axis=(0,0,1),vertices=20)
    leaf=u.group('VariantLeaf',body)
    u.mesh('Small leaf badge',[(.312,1.661,-.026),(.382,1.74,-.014),(.418,1.754,-.047),(.389,1.674,-.039)],[(0,1,2,3)],'leaf_light',leaf)
    for part in [scarf,badge,leaf]:part.hide_render=True
    return root
