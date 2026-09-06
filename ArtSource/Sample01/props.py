"""Editable Sample01 props. All input geometry uses Unity metres, +Y up, +Z front.

The caller owns Blender scene setup, materials, export, and optional mesh joining.
build_props(u) only creates objects through the supplied modelling utilities.
"""

import math
import random


def _add(a, b):
    return tuple(a[i] + b[i] for i in range(3))


def _sub(a, b):
    return tuple(a[i] - b[i] for i in range(3))


def _mul(a, scale):
    return tuple(value * scale for value in a)


def _dot(a, b):
    return sum(a[i] * b[i] for i in range(3))


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _unit(a):
    length = math.sqrt(_dot(a, a))
    return _mul(a, 1.0 / length) if length > 1e-9 else (1.0, 0.0, 0.0)


def _tube(u, name, points, radii, color, parent, sides=8, closed=False):
    """A capped polygon tube; joints remain one editable mesh per finger/cord."""
    vertices, faces = [], []
    count = len(points)
    for index, point in enumerate(points):
        before = points[(index - 1) % count] if closed or index else point
        after = points[(index + 1) % count] if closed or index < count - 1 else point
        tangent = _unit(_sub(after, before))
        reference = (0.0, 1.0, 0.0) if abs(tangent[1]) < .9 else (0.0, 0.0, 1.0)
        first = _unit(_cross(tangent, reference))
        second = _cross(tangent, first)
        radius = radii[index] if isinstance(radii, (tuple, list)) else radii
        for side in range(sides):
            angle = side * math.tau / sides
            vertices.append(_add(point, _add(_mul(first, math.cos(angle) * radius),
                                             _mul(second, math.sin(angle) * radius))))
    for index in range(count if closed else count - 1):
        following = (index + 1) % count
        for side in range(sides):
            other = (side + 1) % sides
            faces.append((index * sides + side, index * sides + other,
                          following * sides + other, following * sides + side))
    if not closed:
        faces.append(tuple(reversed(range(sides))))
        faces.append(tuple((count - 1) * sides + side for side in range(sides)))
    return u.mesh(name, vertices, faces, color, parent=parent, smooth=True)


def _rock_geometry(size, seed):
    """Deterministic chipped rock, normalized to its requested centred bounds."""
    rng = random.Random(seed)
    sides = 9
    vertices = [(0.04, -.5, -.03)]
    for level, y in enumerate((-.31, .025, .32)):
        for index in range(sides):
            angle = index * math.tau / sides + level * .13
            radius = (.72 if level == 0 else 1.0 if level == 1 else .77) * rng.uniform(.85, 1.13)
            vertices.append((math.cos(angle) * radius, y + rng.uniform(-.07, .07),
                             math.sin(angle) * radius * rng.uniform(.90, 1.10)))
    vertices.append((-.08, .5, .025))
    faces = []
    for index in range(sides):
        other = (index + 1) % sides
        faces.append((0, 1 + other, 1 + index))
        for level in range(2):
            a, b = 1 + level * sides + index, 1 + level * sides + other
            c, d = a + sides, b + sides
            faces.extend(((a, b, d), (a, d, c)))
        faces.append((1 + 2 * sides + index, 1 + 2 * sides + other, len(vertices) - 1))
    minimum = [min(point[axis] for point in vertices) for axis in range(3)]
    maximum = [max(point[axis] for point in vertices) for axis in range(3)]
    vertices = [tuple(((point[axis] - minimum[axis]) / (maximum[axis] - minimum[axis]) - .5)
                      * size[axis] for axis in range(3)) for point in vertices]
    # Orient each facet outward independently; triangulation exposes broad rock planes.
    oriented = []
    for face in faces:
        a, b, c = (vertices[index] for index in face)
        normal = _cross(_sub(b, a), _sub(c, a))
        centre = _mul(_add(_add(a, b), c), 1.0 / 3.0)
        oriented.append(tuple(reversed(face)) if _dot(normal, centre) < 0 else face)
    return vertices, oriented


def _rock(u, name, size, seed, parent, colours=("stone", "stone_light", "stone_dark"), offset=(0, 0, 0)):
    vertices, faces = _rock_geometry(size, seed)
    vertices = [_add(vertex, offset) for vertex in vertices]
    for colour_index, colour in enumerate(colours):
        selected = [face for index, face in enumerate(faces) if (index * 7 + seed) % len(colours) == colour_index]
        u.mesh(name + " / " + colour, vertices, selected, colour, parent=parent)
    return vertices, faces


def _bary(a, b, c, wa, wb):
    return _add(_add(_mul(a, wa), _mul(b, wb)), _mul(c, 1.0 - wa - wb))


def _veins(u, parent, vertices, faces, size, seed, colour, edge, count=13):
    rng = random.Random(seed)
    selected = rng.sample(range(len(faces)), min(count, len(faces)))
    for number, index in enumerate(selected):
        a, b, c = (vertices[corner] for corner in faces[index])
        normal = _unit(_cross(_sub(b, a), _sub(c, a)))
        # Readable broad mineral seams follow rock planes. Their winding must
        # face outward even when exported as a separate open colour patch.
        points = [_bary(a, b, c, .05, .08), _bary(a, b, c, .08, .45),
                  _bary(a, b, c, .80, .12), _bary(a, b, c, .85, .05)]
        points = [_add(point, _mul(normal, .0012)) for point in points]
        points = [tuple(max(-size[axis] / 2, min(size[axis] / 2, point[axis]))
                        for axis in range(3)) for point in points]
        if _dot(_cross(_sub(points[1],points[0]),_sub(points[2],points[0])),normal)<0:points.reverse()
        u.mesh("Embedded vein %02d" % number, points, [(0, 1, 2, 3)],
               edge if number % 3 == 0 else colour, parent=parent)
        if number % 3 == 1 or colour == "gold":
            ridge = _add(_mul(_add(points[1], points[2]), .5), _mul(normal, .028 if colour=="gold" else .009))
            ridge = tuple(max(-size[axis] / 2, min(size[axis] / 2, ridge[axis])) for axis in range(3))
            ridge_points=[points[0], points[1], ridge, points[2], points[3]]
            ridge_faces=[(0,1,2),(1,3,2),(3,4,2),(4,0,2)]
            ridge_faces=[tuple(reversed(f)) if _dot(_cross(_sub(ridge_points[f[1]],ridge_points[f[0]]),_sub(ridge_points[f[2]],ridge_points[f[0]])),normal)<0 else f for f in ridge_faces]
            u.mesh("Vein fractured ridge %02d" % number, ridge_points,ridge_faces,colour,parent=parent)


def _pickaxe(u):
    root = u.group("Pickaxe")
    u.cylinder("Ash handle", (0, .15, 0), .025, .66, "wood", parent=root, vertices=12)
    u.cylinder("Handle end ferrule", (0, -.167, 0), .028, .026, "iron", parent=root)
    u.cylinder("Leather grip", (0, -.018, 0), .032, .208, "leather_dark", parent=root, vertices=16)
    for index in range(8):
        u.cylinder("Grip leather wrap %02d" % index, (0, -.104 + index * .025, 0),
                   .034, .009, "leather", parent=root, vertices=16)
    for y in (-.107, .079):
        u.cylinder("Grip fastening rivet", (0, y, .034), .008, .005,
                   "iron_edge", parent=root, axis=(0, 0, 1))
    # Forged curved head, tapered independently in height and depth to both points.
    stations = [(-.325, .345, .002, .002), (-.255, .427, .019, .016),
                (-.145, .480, .030, .026), (0, .479, .042, .039),
                (.125, .472, .030, .027), (.248, .425, .018, .016), (.325, .334, .002, .002)]
    vertices = []
    for x, y, half_height, half_depth in stations:
        vertices.extend(((x, y + half_height, 0), (x, y, half_depth),
                         (x, y - half_height, 0), (x, y, -half_depth)))
    faces, blades = [], []
    for index in range(len(stations) - 1):
        for side in range(4):
            face = (index * 4 + side, index * 4 + (side + 1) % 4,
                    (index + 1) * 4 + (side + 1) % 4, (index + 1) * 4 + side)
            (blades if side == 1 or side == 2 else faces).append(face)
    faces.extend(((3, 2, 1, 0), (24, 25, 26, 27)))
    u.mesh("Forged curved pick crown", vertices, faces, "iron", parent=root)
    u.mesh("Honed pick bevel faces", vertices, blades, "iron_edge", parent=root)
    u.box("Head socket collar", (0, .439, 0), (.090, .112, .092), "iron", parent=root, bevel=.011)
    for z in (-.049, .049):
        u.cylinder("Socket pin", (0, .459, z), .015, .006, "iron_edge", parent=root, axis=(0, 0, 1))
    return root


def _detector(u):
    root = u.group("Detector")
    u.box("Rubber handgrip", (0, -.045, 0), (.069, .19, .082), "leather_dark", parent=root, bevel=.018)
    for index in range(5):
        u.box("Handgrip rib", (0, -.112 + index * .029, .042), (.072, .008, .011),
              "iron", parent=root, bevel=.003)
    u.box("Yellow compact detector shell", (0, .16, .02), (.23, .218, .132), "gold", parent=root, bevel=.024)
    u.box("Detector rear cover", (0, .16, -.049), (.192, .182, .020), "gold_light", parent=root, bevel=.014)
    for x in (-.097, .097):
        for y in (.072, .248):
            u.box("Rubber corner guard", (x, y, .021), (.042, .049, .139), "leather_dark", parent=root, bevel=.012)
    u.box("Screen bevel", (0, .178, .091), (.180, .131, .017), "iron", parent=root, bevel=.012)
    u.box("Screen glass", (0, .178, .102), (.153, .102, .009), "screen", parent=root, bevel=.005)
    wave = [(-.065, .175, .108), (-.046, .175, .108), (-.033, .191, .108),
            (-.021, .153, .108), (-.006, .211, .108), (.010, .167, .108),
            (.025, .184, .108), (.040, .175, .108), (.064, .175, .108)]
    for index in range(len(wave) - 1):
        u.beam("Green waveform %02d" % index, wave[index], wave[index + 1], .005, .003,
               "signal", parent=root, bevel=.001)
    for x in (-.051, 0, .051):
        u.cylinder("Detector control button", (x, .082, .094), .012, .014,
                   "signal" if x == 0 else "iron", parent=root, axis=(0, 0, 1))
    u.box("Short sensor rim", (0, .274, .012), (.15, .014, .072), "iron_edge", parent=root, bevel=.005)
    return root


def _glove(u, name, side):
    root = u.group(name)
    u.ellipsoid("Rounded leather palm back", (0, 0, -.060), (.16, .19, .050),
                "leather", parent=root, segments=16, rings=10)
    u.ellipsoid("Palm heel padding", (-side * .018, -.064, -.060), (.12, .057, .045),
                "leather_dark", parent=root)
    # Four curled fingers leave a continuous opening around the Y-aligned handle.
    for index, y in enumerate((-.061, -.020, .021, .060)):
        points = [(side * .052, y, -.030), (side * .078, y + .002, -.002),
                  (side * .064, y, .035), (side * .033, y - .001, .055),
                  (side * .004, y - .002, .046)]
        _tube(u, "Curved grip finger %d" % (index + 1), points,
              (.017, .0185, .018, .017, .014), "leather", root)
        u.ellipsoid("Finger knuckle seam %d" % (index + 1), (side * .071, y, -.021),
                    (.025, .008, .014), "leather_dark", parent=root, segments=10, rings=6)
    thumb = [(-side * .067, -.048, -.023), (-side * .082, -.016, .011),
             (-side * .060, .012, .040), (-side * .030, .026, .043)]
    _tube(u, "Opposing curled thumb", thumb, (.023, .023, .021, .017), "leather", root)
    u.box("Stitched back patch", (0, .001, -.089), (.089, .105, .011), "leather_dark", parent=root, bevel=.013)
    for x in (-.034, .034):
        for y in (-.035, -.015, .005, .025, .045):
            u.box("Patch stitch", (x, y, -.097), (.005, .009, .003), "canvas", parent=root, bevel=.001)
    # The wrist retreats behind the grip so a Y-aligned pick handle can continue
    # below the palm without disappearing through a solid cuff or forearm.
    u.cylinder("Glove rolled cuff", (0, -.113, -.110), .066, .053, "leather_dark", parent=root, vertices=16)
    u.cylinder("Cuff seam", (0, -.129, -.110), .069, .008, "leather", parent=root, vertices=16)
    u.cylinder("Green exposed forearm", (0, -.238, -.110), .046, .2, "skin", parent=root, vertices=16)
    u.cylinder("Short jacket sleeve end", (0, -.322, -.110), .052, .036, "cloth", parent=root, vertices=16)
    return root


def _ore(u, name, seed, material=None):
    root = u.group(name)
    size = (.55, .45, .5)
    vertices, faces = _rock(u, "Broken host rock", size, seed, root)
    if material is not None:
        _veins(u, root, vertices, faces, size, seed + 81, material,
               "gold_light" if material == "gold" else "iron_edge", 17 if material == "gold" else 13)
    return root


def _raw_shell():
    """Clip a common rock at the inspection plane; preserve a real open surface."""
    source, source_faces = _rock_geometry((.55, .45, .5), 4107)
    vertices, faces, boundary, lookup = [], [], [], {}
    cut = .145

    def vertex(point):
        key = tuple(round(value, 10) for value in point)
        if key not in lookup:
            lookup[key] = len(vertices)
            vertices.append(point)
        return lookup[key]

    for face in source_faces:
        polygon = [source[index] for index in face]
        clipped = []
        for index, point in enumerate(polygon):
            before = polygon[index - 1]
            inside, before_inside = point[2] <= cut, before[2] <= cut
            if inside != before_inside:
                fraction = (cut - before[2]) / (point[2] - before[2])
                crossing = _add(before, _mul(_sub(point, before), fraction))
                clipped.append(crossing)
                boundary.append(vertex(crossing))
            if inside:
                clipped.append(point)
        if len(clipped) >= 3:
            faces.append(tuple(vertex(point) for point in clipped))
    size = (.55, .45, .5)
    minimum = [min(point[axis] for point in vertices) for axis in range(3)]
    maximum = [max(point[axis] for point in vertices) for axis in range(3)]
    vertices = [tuple(((point[axis] - minimum[axis]) / (maximum[axis] - minimum[axis]) - .5)
                      * size[axis] for axis in range(3)) for point in vertices]
    outline = [vertices[index] for index in sorted(set(boundary))]
    centre = _mul(tuple(sum(point[axis] for point in outline) for axis in range(3)), 1.0 / len(outline))
    outline.sort(key=lambda point: math.atan2(point[1] - centre[1], point[0] - centre[0]))
    return vertices, faces, outline, centre


def _raw(u, name, state, mineral="stone"):
    root = u.group(name)
    vertices, faces, outline, centre = _raw_shell()
    for index, colour in enumerate(("stone", "stone_dark", "stone_light")):
        u.mesh("Raw stone shell / " + colour, vertices,
               [face for number, face in enumerate(faces) if number % 3 == index], colour, parent=root)
    count = len(outline)
    if state == "closed":
        # A continuous, unmarked rough front: no inspection rim or gold hint.
        rough_centre = (centre[0] - .017, centre[1] + .014, .239)
        for index in range(count):
            u.mesh("Unbroken raw rock facet %02d" % index,
                   [rough_centre, outline[index], outline[(index + 1) % count]], [(0, 1, 2)],
                   ("stone", "stone_dark", "stone_light")[index % 3], parent=root)
        return root

    scale = .38 if state == "window" else .86
    lip = [(centre[0] + (point[0] - centre[0]) * scale,
            centre[1] + (point[1] - centre[1]) * scale, .246) for point in outline]
    inner = [(centre[0] + (point[0] - centre[0]) * .91,
              centre[1] + (point[1] - centre[1]) * .91, .226) for point in lip]
    for index in range(count):
        following = (index + 1) % count
        u.mesh("Rock around cut %02d" % index,
               [outline[index], outline[following], lip[following], lip[index]], [(0, 1, 2, 3)],
               "stone" if index % 3 else "stone_dark", parent=root)
        u.mesh("Chipped cut bevel %02d" % index,
               [lip[index], lip[following], inner[following], inner[index]], [(0, 1, 2, 3)],
               "stone_light", parent=root)
        core_centre = (centre[0], centre[1], .220)
        # Window deliberately stays neutral for every private mineral kind.
        colour = ("stone", "stone_light")[index % 2] if state == "window" else mineral
        if state == "open" and index % 4 == 1:
            colour = {"gold": "gold_light", "iron": "iron_edge", "stone": "stone_light"}[mineral]
        u.mesh("Revealed interior facet %02d" % index,
               [core_centre, inner[index], inner[following]], [(0, 1, 2)], colour, parent=root)
    return root


def _heavy(u):
    root = u.group("HeavyOre")
    _rock(u, "Heavy broken grey rock", (1.08, .79, .89), 8113, root)
    for x in (-.29, .29):
        points = [(x, math.cos(index * math.tau / 20) * .411,
                   math.sin(index * math.tau / 20) * .461) for index in range(20)]
        _tube(u, "Heavy cargo rope loop", points, .012, "canvas", root, sides=8, closed=True)
        # A second thin strand gives the binding a readable twisted construction.
        strand = [(point[0] + .010 * math.sin(index * 2.8), point[1], point[2])
                  for index, point in enumerate(points)]
        _tube(u, "Rope twisted edge", strand, .0045, "wood_light", root, sides=6, closed=True)
    points = [(math.cos(index * math.tau / 22) * .532, -.045,
               math.sin(index * math.tau / 22) * .451) for index in range(22)]
    _tube(u, "Horizontal cargo binding", points, .011, "canvas", root, sides=8, closed=True)
    for offset in (-.014, .014):
        _tube(u, "Rope knot", [(.29 + offset, .373, .17), (.31 + offset, .397, .10),
                               (.26 + offset, .398, .045), (.25 + offset, .366, .105),
                               (.30 + offset, .35, .15)], .012, "canvas", root)
    return root


def _crate(u):
    root = u.group("WoodCrate")
    for side in (-1, 1):
        for index in range(5):
            coordinate = -.376 + index * .188
            u.box("Front back individual plank", (0, coordinate, side * .4725), (.91, .175, .055),
                  "wood" if index % 2 else "wood_light", parent=root, bevel=.006)
            u.box("Side individual plank", (side * .4725, coordinate, 0), (.055, .175, .91),
                  "wood" if index % 2 else "wood_dark", parent=root, bevel=.006)
            u.box("Top bottom individual plank", (coordinate, side * .4725, 0), (.175, .055, .91),
                  "wood_light" if index % 2 else "wood", parent=root, bevel=.006)
    for x in (-.45, .45):
        for z in (-.45, .45):
            u.box("Crate corner post", (x, 0, z), (.085, .98, .085), "wood_dark", parent=root, bevel=.006)
    for side in (-1, 1):
        u.beam("Face diagonal timber brace", (-.398, -.398, side * .484), (.398, .398, side * .484),
               .067, .013, "wood_light", parent=root, bevel=.004)
        for x in (-.447, .447):
            for y in (-.43, .43):
                u.box("Iron corner strap", (x, y, side * .486), (.070, .104, .013),
                      "iron", parent=root, bevel=.003)
                u.cylinder("Crate iron nail", (x, y, side * .495), .012, .006,
                           "iron_edge", parent=root, axis=(0, 0, 1), vertices=8)
    return root


def _ladder(u):
    root = u.group("Ladder")
    for side in (-1, 1):
        u.box("Ladder wood side beam", (side * .345, 0, 0), (.11, 2.15, .115),
              "wood", parent=root, bevel=.014)
        u.box("Ladder rail worn edge", (side * .345, 0, .060), (.064, 2.10, .006),
              "wood_light", parent=root, bevel=.002)
    for rung, y in enumerate((-.75, -.25, .25, .75)):
        u.box("Ladder rung %d" % (rung + 1), (0, y, .015), (.79, .106, .135),
              "wood_light", parent=root, bevel=.013)
        for side in (-1, 1):
            for loop in (-.015, .015):
                points = [(side * .345 + loop, y + math.cos(index * math.tau / 12) * .076,
                           .012 + math.sin(index * math.tau / 12) * .079) for index in range(12)]
                _tube(u, "Rung rope lashing", points, .006, "canvas", root, sides=6, closed=True)
            _tube(u, "Lashing diagonal", [(side * .311, y - .055, .092),
                                           (side * .345, y, .098), (side * .379, y + .055, .092)],
                  .007, "wood_dark", root, sides=6)
    return root


def build_props(u):
    """Return asset-name -> editable root, without changing caller scene settings."""
    assets = {"Pickaxe": _pickaxe(u), "Detector": _detector(u),
              "GloveRight": _glove(u, "GloveRight", 1), "GloveLeft": _glove(u, "GloveLeft", -1)}
    for name, seed, mineral in (("GoldOre", 2001, "gold"), ("StoneOre", 2017, None),
                                ("IronOre", 2039, "iron")):
        assets[name] = _ore(u, name, seed, mineral)
    for name, state, mineral in (("RawClosed", "closed", "stone"), ("RawWindow", "window", "stone"),
                                  ("RawOpenGold", "open", "gold"), ("RawOpenIron", "open", "iron"),
                                  ("RawOpenStone", "open", "stone")):
        assets[name] = _raw(u, name, state, mineral)
    assets["HeavyOre"] = _heavy(u)
    assets["WoodCrate"] = _crate(u)
    assets["Ladder"] = _ladder(u)
    return assets
