"""Editable camp-corner samples; all coordinates are Unity metres, Y up, +Z front.

CabinCorner faces -Z. Its wall planes match x=+/-2.1, z=0..4, y=0..2.9;
the central x=(-.825,.825) passage stays open below the 2.51 m lintel.
The caller retains the existing cabin/cabinet and 1.6 x 1.1 x 1 stall colliders.
No scene setup, lights, colliders, export, file writes or external prop dependency.
Only the supplied shared-atlas modelling helpers create Blender objects.
"""

import math
import random


def _add(a, b):
    return tuple(a[i] + b[i] for i in range(3))


def _slab(u, name, surface, thickness, color, parent):
    """Closed four-corner panel, thickness measured downward in Unity Y."""
    lower = [(x, y - thickness, z) for x, y, z in surface]
    return u.mesh(name, list(surface) + lower,
                  [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                   (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)],
                  color, parent)


def _stone(u, name, center, size, seed, colors, parent):
    """Closed, chipped rings; normalized dimensions bound every generated vertex."""
    rng = random.Random(seed)
    sides = 7
    vertices = []
    for row, (height, radius) in enumerate(((-.5, .71), (-.17, 1), (.27, .88), (.49, .5))):
        for side in range(sides):
            angle = math.tau * side / sides + (row % 2) * .13
            reach = radius * rng.uniform(.80, 1)
            vertices.append(_add(center, (math.cos(angle) * reach * size[0] * .5,
                                           height * size[1],
                                           math.sin(angle) * reach * size[2] * .5)))
    faces = [tuple(reversed(range(sides)))]
    for row in range(3):
        for side in range(sides):
            nxt = (side + 1) % sides
            a, b, c, d = row*sides+side, row*sides+nxt, (row+1)*sides+nxt, (row+1)*sides+side
            if (side + row) % 3 == 1:
                faces.extend(((a, b, c), (a, c, d)))
            else:
                faces.append((a, b, c, d))
    faces.append(tuple(3*sides+side for side in range(sides)))
    return u.mesh(name, vertices, faces, colors, parent)


def _lantern(u, name, parent, position, scale=1):
    """Small mesh-only lamp. Origin is the light-coloured glass centre."""
    root = u.group(name, parent, position)
    def p(x, y, z):
        return (x*scale, y*scale, z*scale)
    u.box(name+' warm glass', p(0, 0, 0), p(.16, .24, .16), 'lamp', root, .018*scale)
    for x in (-.102, .102):
        for z in (-.102, .102):
            u.beam(name+' iron upright', p(x, -.158, z), p(x, .158, z),
                   .027*scale, .027*scale, 'iron', root, .004*scale)
    for y in (-.158, .146):
        u.box(name+' square rim', p(0, y, 0), p(.24, .034, .24), 'iron', root, .008*scale)
    u.lathe(name+' peaked cap', [.16*scale, .226*scale, .245*scale],
            [.151*scale, .052*scale, .035*scale], [.151*scale, .052*scale, .035*scale],
            'iron', root, segments=4, start=45, end=405, smooth=False)
    u.beam(name+' hanging eye', p(0, .243, 0), p(0, .35, 0),
           .023*scale, .023*scale, 'iron', root, .004*scale)
    u.beam(name+' wall bracket', p(0, .34, 0), p(0, .34, .25),
           .032*scale, .032*scale, 'iron', root, .004*scale)
    return root


def _barrel(u, name, parent, position):
    """A small independent stave barrel; no dependency on props.build_props."""
    root = u.group(name, parent, position)
    sides = 12
    profile = ((0, .245), (.10, .281), (.39, .315), (.68, .28), (.76, .244))
    for side in range(sides):
        angle = math.tau * side / sides
        half = math.pi / sides - .012
        vertices = []
        for height, radius in profile:
            for edge in (-half, half):
                vertices.append((math.sin(angle+edge)*radius, height, math.cos(angle+edge)*radius))
        faces = [(row*2, row*2+1, row*2+3, row*2+2) for row in range(len(profile)-1)]
        u.mesh(name+' stave %02d'%side, vertices, faces,
               ('wood', 'wood_light', 'wood', 'wood_dark')[side % 4], root)
    for height, radius in ((.10, .29), (.63, .298)):
        u.lathe(name+' iron hoop', [height-.034, height+.034], [radius, radius],
                [radius, radius], 'iron', root, segments=12, smooth=False)
    u.cylinder(name+' recessed lid', (0, .746, 0), .233, .018, 'wood_dark', root, vertices=12)
    for x in (-.13, -.044, .044, .13):
        length = 2*math.sqrt(.222**2-x*x)
        u.box(name+' lid board', (x, .76, 0), (.080, .023, length), 'wood', root, .004)
    return root


def _front_window(u, parent, name, x, y, z, width=.82, height=.96):
    """A shallow exterior frame, never a second doorway or an interior obstacle."""
    u.box(name+' recess', (x, y, z), (width+.13, height+.13, .065), 'wood_dark', parent, .018)
    for dx in (-1, 1):
        for dy in (-1, 1):
            u.box(name+' amber pane', (x+dx*width*.24, y+dy*height*.24, z-.041),
                  (width*.43, height*.43, .025), 'window', parent, .008)
    for dx in (-1, 1):
        u.box(name+' jamb', (x+dx*(width+.04)*.5, y, z-.068),
              (.074, height+.16, .08), 'wood_light', parent, .01)
    for dy in (-1, 1):
        u.box(name+' rail', (x, y+dy*(height+.04)*.5, z-.07),
              (width+.19, .08, .095), 'wood_light', parent, .012)
    u.box(name+' mullion', (x, y, z-.068), (.046, height, .065), 'wood_dark', parent, .005)
    u.box(name+' transom', (x, y, z-.07), (width, .046, .07), 'wood_dark', parent, .005)
    u.box(name+' sill', (x, y-height*.5-.075, z-.08),
          (width+.24, .095, .21), 'wood_light', parent, .015)


def _cabin(u):
    root = u.group('CabinCorner')
    # No door leaf, front stair, front brace or furnishing enters the established
    # door/cabinet access path. The low board deck matches the old visual porch.
    for column in range(14):
        x = -1.97 + column*(3.94/13)
        u.box('Cabin deck plank %02d'%column, (x, .023, 1.76),
              (.289, .046, 4.40), ('wood', 'wood_light', 'wood')[column % 3], root, .005)
    for row in range(9):
        y = .174 + row*.316
        color = ('wood', 'wood_light', 'wood', 'wood', 'wood_light')[row % 5]
        u.box('Cabin back plank %02d'%row, (0, y, 4), (4.2, .296, .19), color, root, .012)
        for side in (-1, 1):
            u.box('Cabin side plank %d %02d'%(side, row), (side*2.10, y, 2),
                  (.19, .296, 4.0), color, root, .012)
            u.box('Cabin front plank %d %02d'%(side, row), (side*1.4625, y, 0),
                  (1.275, .296, .19), color, root, .012)
    # Real shadow seams come from the separate boards, with restrained split seams
    # and a few woodgrain grooves rather than a regular painted stripe pattern.
    for row, side, offset in ((1,-1,.14), (3,1,-.18), (5,-1,-.22), (7,1,.12)):
        u.box('Cabin front board joint', (side*1.46+offset, .174+row*.316, -.101),
              (.012, .266, .008), 'wood_dark', root, .001)
    for row in (0, 3, 6):
        u.beam('Cabin front grain', (-1.98, .14+row*.316, -.103),
               (-1.17, .155+row*.316, -.104), .007, .004, 'wood_dark', root, .001)
    for side in (-1, 1):
        for z in (-.055, 4.04):
            u.box('Cabin corner upright', (side*2.10, 1.48, z),
                  (.23, 2.96, .24), 'wood_dark', root, .025)
        # Frame inner edges +/- .835, outside the +/- .825 collision opening.
        u.box('Cabin door jamb', (side*.94, 1.29, -.113),
              (.21, 2.58, .23), 'wood_light', root, .022)
        u.box('Cabin door iron foot', (side*.94, .175, -.237),
              (.20, .22, .025), 'iron', root, .008)
        u.cylinder('Cabin jamb nail', (side*.94, .18, -.259), .026, .018,
                   'iron_edge', root, axis=(0,0,-1), vertices=8)
        u.beam('Cabin front knee brace', (side*2.04, 2.76, -.19),
               (side*1.38, 2.11, -.19), .12, .105, 'wood_light', root, .015)
        u.beam('Cabin side knee brace', (side*2.225, 2.71, .10),
               (side*2.225, 2.10, .74), .12, .10, 'wood_light', root, .015)
    u.box('Cabin door lintel', (0, 2.65, -.13), (2.18, .28, .27), 'wood_dark', root, .026)
    u.box('Cabin top wall infill', (0, 2.87, .005), (1.66, .08, .18), 'wood', root, .008)
    for z in (-.14, 4.12):
        u.box('Cabin gable tie beam', (0, 2.92, z), (4.53, .20, .23), 'wood_dark', root, .022)
        for row in range(5):
            low, high = 3.02+row*.225, 3.227+row*.225
            high = min(high, 4.17)
            if high <= low:
                continue
            half_low = (4.20-low)/.50
            half_high = max(.04, (4.20-high)/.50)
            front = [(-half_low,low,z), (half_low,low,z), (half_high,high,z), (-half_high,high,z)]
            vertices = front + [(x,y,z+.12) for x,y,z in front]
            u.mesh('Cabin gable board', vertices,
                   [(0,1,2,3),(7,6,5,4),(0,4,5,1),(1,5,6,2),(2,6,7,3),(3,7,4,0)],
                   ('wood', 'wood_light')[row % 2], root)
        for side in (-1, 1):
            u.beam('Cabin gable sloped beam', (side*2.34, 2.985, z-.035),
                   (0,4.18,z-.035), .13, .16, 'wood_dark', root, .014)
        u.beam('Cabin gable king post', (0,2.96,z-.03), (0,4.12,z-.03),
               .13,.15,'wood_dark',root,.014)

    def roof_y(x):
        return 4.25-abs(x)*.50
    for side in (-1, 1):
        _slab(u, 'Cabin thick roof underlay',
              [(0,roof_y(0),-.54),(side*2.52,roof_y(2.52),-.54),
               (side*2.52,roof_y(2.52),4.48),(0,roof_y(0),4.48)],
              .13, 'wood_dark', root)
        for row in range(5):
            near, far = row*.50, min(2.56, (row+1)*.50+.055)
            for column in range(9):
                z0 = -.55+column*.56
                z1 = min(4.49, z0+.545)
                lift = .038 + (4-row)*.008
                # Individual tapered shingles overlap down the slope. Staggered
                # seam caps and warm dark undersides give thickness at the eaves.
                x0, x1 = side*near, side*far
                surface = [(x0,roof_y(x0)+lift,z0+.014*(column%2)),
                           (x1,roof_y(x1)+lift,z0),
                           (x1,roof_y(x1)+lift-.01,z1),
                           (x0,roof_y(x0)+lift,z1-.012*(column%3))]
                color = 'roof_light' if (column+row*2)%5 == 0 else 'roof'
                _slab(u, 'Cabin roof shingle %d %d %d'%(side,row,column),
                      surface, .055, color, root)
        u.beam('Cabin eave fascia', (side*2.55,2.95,-.59), (side*2.55,2.95,4.53),
               .12,.18,'wood_dark',root,.012)
    for column in range(9):
        z0, z1 = -.57+column*.57, -.57+column*.57+.56
        _slab(u, 'Cabin ridge cap L', [(0,4.365,z0),(-.18,4.29,z0),(-.18,4.29,z1),(0,4.365,z1)],
              .045,'roof_light',root)
        _slab(u, 'Cabin ridge cap R', [(0,4.365,z0),(.18,4.29,z0),(.18,4.29,z1),(0,4.365,z1)],
              .045,'roof',root)
    _front_window(u, root, 'Cabin right window', 1.49, 1.73, -.115, .76, .89)
    _lantern(u, 'Cabin door lantern', root, (-1.47,1.98,-.42))
    _barrel(u, 'Cabin side barrel', root, (-2.70,0,.34))
    # Small paired chimney segments are confined to the rear roof, not the entry.
    u.box('Cabin chimney lower', (1.23,3.80,3.31), (.36,.70,.38), 'stone_dark', root, .035)
    u.box('Cabin chimney upper', (1.23,4.29,3.31), (.34,.37,.35), 'stone', root, .025)
    u.box('Cabin chimney cap', (1.23,4.495,3.31), (.46,.075,.47), 'stone_dark', root, .016)
    return root


def _canopy(u, root):
    def height(x, z):
        return 2.92-.18*(1-(x/1.67)**2)-.06*(1-(z/1.02)**2)
    columns, rows = 10, 7
    xs = [-1.67+i*3.34/(columns-1) for i in range(columns)]
    zs = [-1.02+j*2.04/(rows-1) for j in range(rows)]
    top = [(x,height(x,z),z) for z in zs for x in xs]
    count = len(top)
    vertices = top+[(x,y-.016,z) for x,y,z in top]
    faces = []
    for j in range(rows-1):
        for i in range(columns-1):
            a=j*columns+i
            faces.extend(((a,a+1,a+1+columns,a+columns),
                          (a+count+columns,a+count+columns+1,a+count+1,a+count)))
    boundary = list(range(columns)) + [j*columns+columns-1 for j in range(1,rows)]
    boundary += list(range((rows-1)*columns+columns-2,(rows-1)*columns-1,-1))
    boundary += [j*columns for j in range(rows-2,0,-1)]
    for index,a in enumerate(boundary):
        b=boundary[(index+1)%len(boundary)]
        faces.append((a,b,b+count,a+count))
    u.mesh('Market sagging canvas canopy', vertices, faces, 'canvas', root, smooth=True)
    for x in (-.56,.56):
        for index in range(rows-1):
            a,b=zs[index],zs[index+1]
            u.beam('Market stitched canopy seam', (x,height(x,a)+.012,a),
                   (x,height(x,b)+.012,b), .009,.009,'rope',root,.001)
    # A thin closed front valance, with uneven lower edge rather than a rigid cube.
    vertices=[]
    for i,x in enumerate(xs):
        y=height(x,-1.02)
        bottom=y-.20+(.033 if i in (2,7) else 0)
        vertices.extend(((x,y,-1.029),(x,bottom,-1.06),(x,y-.012,-1.009),(x,bottom,-1.042)))
    faces=[]
    for i in range(columns-1):
        a=i*4;b=a+4
        faces.extend(((a,b,b+1,a+1),(a+3,b+3,b+2,a+2),
                      (a+2,b+2,b,a),(a+1,b+1,b+3,a+3)))
    faces.extend(((0,1,3,2),((columns-1)*4,(columns-1)*4+2,(columns-1)*4+3,(columns-1)*4+1)))
    u.mesh('Market irregular canvas valance', vertices, faces, 'canvas', root)


def _scale(u, parent):
    root=u.group('Market small balance scale',parent,(.12,1.24,.13))
    u.cylinder('Scale weighted foot',(0,.025,0),.105,.045,'iron',root,vertices=12)
    u.cylinder('Scale turned brass post',(0,.285,0),.025,.50,'gold',root,vertices=10)
    u.ellipsoid('Scale finial',(0,.565,0),(.077,.10,.077),'gold',root,12,8)
    u.beam('Scale balance arm',(-.29,.51,0),(.29,.49,0),.031,.033,'iron',root,.004)
    for side in (-1,1):
        x=side*.24
        anchor=(x,.5-side*.01,0)
        for k in range(3):
            a=math.tau*k/3
            u.beam('Scale pan chain',anchor,(x+math.cos(a)*.117,.225,math.sin(a)*.117),
                   .009,.009,'gold',root,.001)
        bowl=u.group('Scale shallow pan',root,(x,.17,0))
        u.lathe('Scale pan bowl',[0,.033,.055,.044,.015],
                [.032,.124,.142,.114,.026],[.032,.124,.142,.114,.026],
                'gold',bowl,segments=12,smooth=False)
        u.cylinder('Scale pan bottom',(0,.012,0),.039,.009,'gold',bowl,vertices=12)


def _market(u):
    root=u.group('MarketStall')
    for x in (-1.19,1.19):
        for z in (-.49,.49):
            u.box('Market chunky leg',(x,.555,z),(.20,1.11,.20),'wood_dark',root,.024)
    for side in (-1,1):
        u.box('Market side apron',(side*1.27,.62,0),(.12,.89,1.14),'wood',root,.016)
    for i in range(8):
        x=-1.12+i*.32
        u.box('Market front upright board %d'%i,(x,.60,-.535),(.305,.99,.115),
              ('wood','wood_light','wood','wood')[i%4],root,.014)
    for y in (.16,1.018):
        u.box('Market front apron rail',(0,y,-.605),(2.69,.13,.15),'wood_dark',root,.018)
    u.beam('Market apron diagonal',(-1.13,.25,-.633),(1.13,.955,-.633),
           .105,.052,'wood_light',root,.01)
    for i in range(5):
        u.box('Market tabletop plank %d'%i,(0,1.15,-.56+i*.28),
              (2.80,.16,.27),('wood_light','wood','wood_light')[i%3],root,.017)
    for side in (-1,1):
        for z in (-.58,.58):
            u.box('Market iron corner strap',(side*1.285,1.13,z),(.23,.19,.12),'iron',root,.014)
            u.cylinder('Market corner bolt',(side*1.285,1.12,z+(.07 if z>0 else -.07)),
                       .027,.018,'iron_edge',root,axis=(0,0,1),vertices=8)
        u.box('Market rear canopy upright',(side*1.45,1.48,.61),(.17,2.96,.18),'wood',root,.025)
        u.beam('Market canopy side arm',(side*1.45,2.91,.78),(side*1.45,2.87,-1.04),
               .095,.11,'wood_dark',root,.015)
        u.beam('Market canopy knee brace',(side*1.45,2.20,.61),(side*1.45,2.87,-.11),
               .082,.075,'wood_light',root,.01)
        for y in (2.72,2.76,2.80):
            u.cylinder('Market rope lashing',(side*1.45,y,.61),.12,.024,'rope',root,vertices=12)
        _lantern(u,'Market hanging lantern %d'%side,root,(side*1.43,2.17,-.77),.83)
    for z in (-1.02,1.02):
        u.beam('Market canopy edge rail',(-1.68,2.91,z),(1.68,2.91,z),.08,.095,'wood_dark',root,.012)
    _canopy(u,root)
    _scale(u,root)
    # Display pieces are decorative, with no gameplay IDs or pick-up ownership.
    u.box('Market display tray base',(-.75,1.255,-.13),(.74,.042,.47),'wood_dark',root,.013)
    for x in (-1.135,-.365):
        u.box('Market tray short rim',(x,1.307,-.13),(.045,.12,.50),'wood_light',root,.008)
    for z in (-.39,.13):
        u.box('Market tray long rim',(-.75,1.307,z),(.81,.12,.045),'wood_light',root,.008)
    for i,(x,z,s) in enumerate(((-.98,-.24,.13),(-.78,-.19,.19),(-.56,-.15,.14),
                                (-.84,.015,.12),(-.62,-.31,.13))):
        _stone(u,'Market sample nugget %d'%i,(x,1.29+s*.5,z),(s,s,s*.83),
               412+i,('gold','gold_light','gold'),root)
    u.box('Market little cash box',(.91,1.375,.10),(.40,.27,.35),'wood_dark',root,.024)
    u.box('Market cash box lid',(.91,1.53,.10),(.43,.065,.37),'wood',root,.015)
    for x in (.80,1.02):
        u.box('Market cash box iron band',(x,1.534,.10),(.036,.069,.38),'iron',root,.006)
    u.box('Market cash box latch',(.91,1.39,-.092),(.055,.083,.03),'iron',root,.006)
    # Folded counter cloth stays on the front face, out of the walking surface.
    _slab(u,'Market folded cloth top',[(-.20,1.243,.07),(.25,1.243,.07),
          (.25,1.243,-.70),(-.20,1.243,-.70)],.012,'canvas',root)
    u.mesh('Market cloth over front',
           [(-.20,1.243,-.715),(.25,1.243,-.715),(.22,.795,-.73),(-.17,.76,-.735),
            (-.20,1.231,-.699),(.25,1.231,-.699),(.22,.795,-.714),(-.17,.76,-.719)],
           [(0,1,2,3),(7,6,5,4),(0,4,5,1),(1,5,6,2),(2,6,7,3),(3,7,4,0)],'canvas',root)
    return root


def _pine(u):
    root=u.group('PineSample')
    rng=random.Random(51237)
    sides=9
    vertices=[]
    levels=((0,.16),(.35,.145),(1.3,.116),(2.25,.09),(3.3,.055),(4.25,.023),(4.65,.009))
    for i,(y,radius) in enumerate(levels):
        dx=.027*math.sin(i*.75)
        dz=.022*math.sin(i*1.13)
        for k in range(sides):
            a=math.tau*k/sides
            vertices.append((dx+math.cos(a)*radius,y,dz+math.sin(a)*radius))
    faces=[tuple(reversed(range(sides)))]
    for row in range(len(levels)-1):
        for k in range(sides):
            faces.append((row*sides+k,row*sides+(k+1)%sides,(row+1)*sides+(k+1)%sides,(row+1)*sides+k))
    faces.append(tuple((len(levels)-1)*sides+k for k in range(sides)))
    u.mesh('Pine tapered bark trunk',vertices,faces,('wood_dark','wood','wood_dark'),root)
    for k in range(5):
        a=math.tau*k/5+.3
        u.beam('Pine shallow root flare',(0,.22,0),(math.cos(a)*.32,.018,math.sin(a)*.32),
               .08,.07,'wood_dark',root,.008)
    for tier,(y,reach,count) in enumerate(((1.18,1.08,7),(1.82,.97,7),(2.49,.82,6),
                                          (3.16,.66,6),(3.79,.43,5),(4.38,.23,4))):
        rotation=tier*.57
        for branch in range(count):
            angle=rotation+math.tau*branch/count+rng.uniform(-.10,.10)
            r=reach*rng.uniform(.9,1.06)
            width=r*.46
            direction=(math.cos(angle),math.sin(angle))
            tangent=(-direction[1],direction[0])
            def point(distance,offset,height):
                return (direction[0]*distance+tangent[0]*offset,height,
                        direction[1]*distance+tangent[1]*offset)
            u.beam('Pine visible branch %d %d'%(tier,branch),(0,y+.08,0),
                   point(r*.88,0,y-.13),.023+.022*(1-tier/6),.028,'wood_dark',root,.004)
            # Each branch is its own asymmetric toothed fan. The silhouette has
            # gaps and drooping tips; it is not assembled from smooth cone tiers.
            outline=[point(r*.12,0,y+.1),point(r*.35,width*.65,y+.20),
                     point(r*.60,width,y+.06),point(r*.90,width*.79,y-.21),
                     point(r*.77,width*.29,y-.10),point(r*1.12,.012,y-.34),
                     point(r*.79,-width*.26,y-.12),point(r*.96,-width*.72,y-.25),
                     point(r*.61,-width*.94,y+.04),point(r*.35,-width*.61,y+.19)]
            vertices=outline+[point(r*.48,0,y+.33),point(r*.48,0,y-.07)]
            faces=[]
            colors=[]
            for i in range(10):
                faces.append((10,i,(i+1)%10))
                colors.append('leaf_light' if (branch+tier+i)%5==0 else 'leaf')
            for i in range(10):
                faces.append((11,(i+1)%10,i));colors.append('leaf')
            u.mesh('Pine needle fan %d %d'%(tier,branch),vertices,faces,colors,root)
    u.mesh('Pine irregular leader',[(0,4.80,0),(-.105,4.43,-.06),(.09,4.40,-.075),
           (.115,4.38,.063),(-.074,4.44,.09)],
           [(0,1,2),(0,2,3),(0,3,4),(0,4,1),(4,3,2,1)],('leaf','leaf_light','leaf'),root)
    return root


def _rocks(u):
    root=u.group('RockSample')
    palette=('stone','stone','stone_dark','stone_light','stone')
    _stone(u,'Rock main split block',(-.10,.295,0),(.68,.59,.64),2231,palette,root)
    _stone(u,'Rock low right block',(.30,.17,.12),(.37,.34,.41),2232,palette,root)
    _stone(u,'Rock front chip',(.10,.105,-.33),(.32,.21,.28),2233,palette,root)
    _stone(u,'Rock rear chip',(-.30,.13,.27),(.30,.26,.33),2234,palette,root)
    return root


def build_corner(u):
    """Return four separately exportable roots; all geometry uses the shared atlas."""
    return {'CabinCorner':_cabin(u), 'MarketStall':_market(u),
            'PineSample':_pine(u), 'RockSample':_rocks(u)}
