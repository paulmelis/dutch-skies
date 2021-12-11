#!/usr/bin/env python
from math import sqrt, degrees, radians, sin, cos, exp
from random import random, randint, seed

#seed(123456)

def line_point_distance_xz(p, q, t):
    """Return closest distance between 3D point t
    and the line between points p and q *in the XZ plane*"""
    
    x1 = p[0]
    y1 = p[1]
    z1 = p[2]
    
    x2 = q[0]
    y2 = q[1]
    z2 = q[2]
    
    x0 = t[0]
    y0 = t[1]
    z0 = t[2]
    
    num = abs((x2-x1)*(z1-z0) - (x1-x0)*(z2-z1))    
    den = sqrt((x2-x1)**2 + (z2-z1)**2)
    return num/den
    
def rot_y(p, angle_degrees):
    """Positive rotation = CCW"""
    a = radians(angle_degrees)
    cos_a = cos(a)
    sin_a = sin(a)
    
    qx = cos_a*p[0] + sin_a*p[2]
    qz = -sin_a*p[0] + cos_a*p[2]

    return (qx, p[1], qz)
    
if False:
    print(line_point_distance_xz((0,0,0), (1,0,0), (0.5,0,0)))
    print(line_point_distance_xz((0,0,0), (1,0,0), (0.5,0,1)))
    
    print(line_point_distance_xz((0,0,0), (1,0,1), (0.5,0,0.5)))
    print(line_point_distance_xz((0,0,0), (1,0,1), (0.5,0,2)))

if False:
    print(rot_y((1,0,0), 90))
    print(rot_y((1,0,0), 45))
    print(rot_y((1,0,0), 0))
    
    print(rot_y((0.88,0,0.88), -45))
    
    
def compute_energy(tx, ty, tz, r):
    """RMS"""
    
    e2 = 0.0
    n = 0
    for ob_origin, name, direction in OBSERVATIONS:
        #print(name)
        o2 = rot_y(ob_origin, r)
        o2 = (o2[0] + tx, o2[1] + ty, o2[2] + tz)
        p2 = rot_y((ob_origin[0]+direction[0], ob_origin[1]+direction[1], ob_origin[2]+direction[2]), r)
        p2 = (p2[0]+tx, p2[1]+ty, p2[2]+tz)
        #print(ob_origin, '->', o2)
        #print(direction, '->', p2)
        dist = line_point_distance_xz(o2, p2, GROUND_TRUTH[name])
        #print(dist)
        e2 += dist*dist
        n += 1
        
    return sqrt(e2/n)
    
def rand_unit():
    return (random()-0.5)*2
    
if __name__ == '__main__':

    # Ground truth (in coord system to align to)
    # Point coordinates
    # SK world coordinate system, i.e. +Y up
    GROUND_TRUTH = {
        'RT': (-891.0309, 148, 701.9933), 
        'HKF': (222.3811, 33.5, -189.894100),
        'HKB': (259.083, 30.5, -178.2989),
        'BR': (303.6142, 30.5, 79.835310),
        'DHL': (-476.4835, 64, 254.3338),
        'SS': (-52.751860, 11, -22.81),
        'BOOM': (50.029740, 9, 115.9509)
    }
    
    # Direction vectors from observer position
    # Note: head pos Y (height) is relative to starting head position
    # and not above floor
    OBSERVATIONS = [
        ((1.070687, 0.084614, 3.150276), 'DHL', (0.922612, 0.123531, -0.365413)),
        ((1.187483, 0.075501, 3.316733), 'SS', (0.762326, 0.122043, 0.635582)),
        ((1.207898, 0.053789, 3.258828), 'BOOM', (-0.362435, 0.037529, -0.931253)),
        ((2.160876, 0.044619, -5.497616), 'BOOM', (-0.394080, 0.017386, -0.918912)),
        ((2.198612, 0.063130, -5.415352), 'SS', (0.691618, 0.079084, 0.717921)),
        ((2.145756, 0.059008, -5.169587), 'HKF', (-0.792862, 0.044595, 0.607767)),
        ((1.912785, 0.056219, -5.512104), 'RT', (0.844261, 0.071267, -0.531173))
    ]
    
    """
    print(compute_energy(tx, ty, r))
    print(compute_energy(tx, ty, r+90))
    print(compute_energy(tx, ty, r+180))    
    print(compute_energy(tx, ty, r+270))
    print(compute_energy(-2.972659, -3.278703, 155.837377-180))
    print(compute_energy(-2.972659, -3.278703, 155.837377))    
    """
    
    # Compute the transformation rot(R) followed by T(tx,ty,tz)
    # that maps the sky coordinate system (in which the ground truth
    # points are defined) onto the observation coordinate system
    
    tx = 0.0
    ty = 0.0
    tz = 0.0
    r = 0.0
        
    best_tx = tx
    best_ty = ty
    best_tz = tz
    best_r = r
    best_energy = compute_energy(tx, ty, tz, r)
    print('Energy %.6f (initial) | tx=%.6f, ty=%.6f, tz=%.6f, r=%.6f' % \
        (best_energy, best_tx, best_ty, best_tz, best_r))
    
    K = 40000
    WO = 200
    without_improvement = 0
    for k in range(0, K):
        T = 1 - (k+1)/K
        #print('Iter %d, T = %.6f' % (k, T))
        
        # Pick neighbour
        can_tx = tx
        can_ty = ty
        can_tz = tz
        can_r = r
        
        idx = randint(0, 2)
        if idx == 0:
            #print('Mutating tx')            
            can_tx = tx + rand_unit()*200*T
        # NOTE: ty never mutated
        elif idx == 1:
            #print('Mutating tz')
            can_tz = tz + rand_unit()*200*T
        elif idx == 2:
            #print('Mutating r')
            can_r = (r + rand_unit()*360*T) % 360
            
        energy = compute_energy(can_tx, can_ty, can_tz, can_r)
        #print('Energy %.6f (candidate) | can_tx=%.6f, can_ty=%.6f, can_tz=%6f, can_r=%.6f' % \
        #    (energy, can_tx, can_ty, can_tz, can_r))
            
        if energy <= best_energy:
            print('[%d] Energy %.6f (new best) | tx=%.6f, ty=%.6f, tz=%6f, r=%.6f' % \
                (k, energy, can_tx, can_ty, can_tz, can_r))
            best_energy = energy
            best_tx = can_tx
            best_ty = can_ty
            best_tz = can_tz
            best_r = can_r
            without_improvement = 0
        elif T > 0:
            without_improvement += 1
            if without_improvement == WO:
                # Restart with last best solution
                #print('Restarting')
                tx = best_tx
                ty = best_ty
                tz = best_tz
                r = best_r
                without_improvement = 0
                continue
                
            #print(energy, best_energy, T)
            p = exp(-(energy-best_energy)/T)
            if random() <= p:
                #print('Transitioning to less optimal solution')
                tx = can_tx
                ty = can_ty
                tz = can_tz
                r = can_r
                
    print('Best: tx %.6f, ty %.6f, tz %.6f, r %.6f (energy %.6f)' % \
        (best_tx, best_ty, best_tz, best_r, best_energy))
        
    
